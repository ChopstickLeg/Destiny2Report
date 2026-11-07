use std::{
    fs,
    time::{Duration, Instant},
};

use opentelemetry::metrics::{Meter, ObservableCounter, ObservableGauge};
use tokio_util::sync::CancellationToken;

#[derive(Default)]
struct Snapshot {
    rss_bytes: Option<u64>,
    peak_rss_bytes: Option<u64>,
    container_bytes: Option<u64>,
    container_limit_bytes: Option<u64>,
    container_oom_kills: Option<u64>,
}

impl Snapshot {
    fn read() -> Self {
        let status = fs::read_to_string("/proc/self/status").unwrap_or_default();
        let events = fs::read_to_string("/sys/fs/cgroup/memory.events").unwrap_or_default();
        Self {
            rss_bytes: status_bytes(&status, "VmRSS:"),
            peak_rss_bytes: status_bytes(&status, "VmHWM:"),
            container_bytes: read_number("/sys/fs/cgroup/memory.current"),
            container_limit_bytes: read_number("/sys/fs/cgroup/memory.max"),
            container_oom_kills: field_number(&events, "oom_kill"),
        }
    }

    fn log(&self, phase: &str, event: &str, elapsed: Option<Duration>) {
        if self.rss_bytes.is_some() {
            tracing::info!(
                phase,
                event,
                elapsed_ms = elapsed.map(|value| value.as_millis() as u64),
                rss_bytes = self.rss_bytes,
                peak_rss_bytes = self.peak_rss_bytes,
                container_memory_bytes = self.container_bytes,
                container_memory_limit_bytes = self.container_limit_bytes,
                container_oom_kills = self.container_oom_kills,
                "crawler memory sample"
            );
        }
    }
}

fn read_number(path: &str) -> Option<u64> {
    fs::read_to_string(path).ok()?.trim().parse().ok()
}

fn field_number(text: &str, name: &str) -> Option<u64> {
    text.lines().find_map(|line| {
        let mut fields = line.split_whitespace();
        (fields.next()? == name)
            .then(|| fields.next()?.parse().ok())
            .flatten()
    })
}

fn status_bytes(status: &str, name: &str) -> Option<u64> {
    field_number(status, name)?.checked_mul(1024)
}

pub fn checkpoint(phase: &'static str) {
    Snapshot::read().log(phase, "checkpoint", None);
}

pub struct Phase {
    name: &'static str,
    started: Instant,
}

impl Phase {
    pub fn start(name: &'static str) -> Self {
        Snapshot::read().log(name, "started", None);
        Self {
            name,
            started: Instant::now(),
        }
    }
}

impl Drop for Phase {
    fn drop(&mut self) {
        Snapshot::read().log(self.name, "finished", Some(self.started.elapsed()));
    }
}

pub async fn monitor(cancellation: CancellationToken) {
    let mut interval = tokio::time::interval(Duration::from_secs(30));
    interval.set_missed_tick_behavior(tokio::time::MissedTickBehavior::Skip);
    loop {
        tokio::select! {
            _ = cancellation.cancelled() => break,
            _ = interval.tick() => checkpoint("periodic"),
        }
    }
}

pub struct Metrics {
    _gauges: Vec<ObservableGauge<u64>>,
    _oom_kills: ObservableCounter<u64>,
}

pub fn register_metrics(meter: Meter) -> Metrics {
    type Reading = fn(Snapshot) -> Option<u64>;
    let readings: [(&str, &str, Reading); 4] = [
        (
            "process.memory.usage",
            "Resident set size of this crawler process",
            |sample| sample.rss_bytes,
        ),
        (
            "process.memory.peak_usage",
            "Peak resident set size since process startup",
            |sample| sample.peak_rss_bytes,
        ),
        (
            "crawler.container.memory.usage",
            "Memory charged to the crawler cgroup",
            |sample| sample.container_bytes,
        ),
        (
            "crawler.container.memory.limit",
            "Finite memory limit of the crawler cgroup",
            |sample| sample.container_limit_bytes,
        ),
    ];
    let gauges = readings
        .into_iter()
        .map(|(name, description, reading)| {
            meter
                .u64_observable_gauge(name)
                .with_description(description)
                .with_unit("By")
                .with_callback(move |observer| {
                    if let Some(value) = reading(Snapshot::read()) {
                        observer.observe(value, &[]);
                    }
                })
                .build()
        })
        .collect();
    let oom_kills = meter
        .u64_observable_counter("crawler.container.memory.oom_kills")
        .with_description("OOM kills reported by the crawler cgroup")
        .with_unit("{kill}")
        .with_callback(|observer| {
            if let Some(value) = Snapshot::read().container_oom_kills {
                observer.observe(value, &[]);
            }
        })
        .build();
    Metrics {
        _gauges: gauges,
        _oom_kills: oom_kills,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[cfg(target_os = "linux")]
    #[test]
    fn linux_process_memory_reaches_the_metric_exporter() {
        use opentelemetry::metrics::MeterProvider;
        use opentelemetry_sdk::metrics::{
            InMemoryMetricExporter, SdkMeterProvider,
            data::{AggregatedMetrics, MetricData},
        };
        let exporter = InMemoryMetricExporter::default();
        let provider = SdkMeterProvider::builder()
            .with_periodic_exporter(exporter.clone())
            .build();
        let _instruments = register_metrics(provider.meter("memory-test"));
        provider.force_flush().unwrap();
        let exported = exporter.get_finished_metrics().unwrap();
        for name in ["process.memory.usage", "process.memory.peak_usage"] {
            let metric = exported
                .iter()
                .flat_map(|resource| resource.scope_metrics())
                .flat_map(|scope| scope.metrics())
                .find(|metric| metric.name() == name)
                .expect("process memory metric was not exported");
            assert_eq!(metric.unit(), "By");
            let AggregatedMetrics::U64(MetricData::Gauge(gauge)) = metric.data() else {
                panic!("expected a u64 gauge");
            };
            assert!(gauge.data_points().next().unwrap().value() > 0);
        }
        provider.shutdown().unwrap();
    }

    #[test]
    fn linux_memory_fields_use_bytes_and_exact_names() {
        let status = "Name:\tcrawler\nVmRSS:\t 1234 kB\nVmHWM:\t 5678 kB\n";
        assert_eq!(status_bytes(status, "VmRSS:"), Some(1234 * 1024));
        assert_eq!(status_bytes(status, "VmHWM:"), Some(5678 * 1024));
        assert_eq!(status_bytes(status, "VmSize:"), None);
        assert_eq!(
            field_number("oom 4\noom_kill 2\noom_group_kill 1\n", "oom_kill"),
            Some(2)
        );
    }

    #[test]
    fn missing_or_invalid_memory_readings_are_not_reported_as_zero() {
        assert_eq!(field_number("VmRSS: unknown kB", "VmRSS:"), None);
        assert_eq!(status_bytes("", "VmRSS:"), None);
        assert_eq!(
            status_bytes("VmRSS: 18446744073709551615 kB", "VmRSS:"),
            None
        );
    }
}
