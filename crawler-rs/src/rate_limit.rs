use std::{sync::Arc, time::Duration};

use tokio::{
    sync::{Mutex, Semaphore},
    time::Instant,
};

#[derive(Clone)]
pub struct LocalRateLimiter {
    inner: Arc<Mutex<State>>,
    rate: f64,
    capacity: f64,
    admissions: Arc<Semaphore>,
}

struct State {
    tokens: f64,
    last_refill: Instant,
    paused_until: Instant,
}

impl LocalRateLimiter {
    pub fn per_second(rate: u32, queue_limit: usize) -> Self {
        let now = Instant::now();
        Self {
            inner: Arc::new(Mutex::new(State {
                tokens: rate as f64,
                last_refill: now,
                paused_until: now,
            })),
            rate: rate as f64,
            capacity: rate as f64,
            admissions: Arc::new(Semaphore::new(rate as usize + queue_limit)),
        }
    }

    pub async fn acquire(&self) -> Result<(), ()> {
        let _admission = self
            .admissions
            .clone()
            .try_acquire_owned()
            .map_err(|_| ())?;
        loop {
            let wait = {
                let mut state = self.inner.lock().await;
                let now = Instant::now();
                if now < state.paused_until {
                    state.paused_until - now
                } else {
                    let elapsed = now.duration_since(state.last_refill).as_secs_f64();
                    state.tokens = (state.tokens + elapsed * self.rate).min(self.capacity);
                    state.last_refill = now;
                    if state.tokens >= 1.0 {
                        state.tokens -= 1.0;
                        return Ok(());
                    }
                    Duration::from_secs_f64((1.0 - state.tokens) / self.rate)
                }
            };
            tokio::time::sleep(wait).await;
        }
    }

    pub async fn pause(&self, duration: Duration) {
        let mut state = self.inner.lock().await;
        state.paused_until = state.paused_until.max(Instant::now() + duration);
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[tokio::test(start_paused = true)]
    async fn instances_have_independent_budgets() {
        let left = LocalRateLimiter::per_second(1, 1);
        let right = LocalRateLimiter::per_second(1, 1);
        left.acquire().await.unwrap();
        right.acquire().await.unwrap();
        let blocked = tokio::time::timeout(Duration::from_millis(1), left.acquire()).await;
        assert!(blocked.is_err());
    }

    #[tokio::test(start_paused = true)]
    async fn pause_is_local_to_one_instance() {
        let left = LocalRateLimiter::per_second(1, 1);
        let right = LocalRateLimiter::per_second(1, 1);
        left.pause(Duration::from_secs(30)).await;
        right.acquire().await.unwrap();
        assert!(
            tokio::time::timeout(Duration::from_millis(1), left.acquire())
                .await
                .is_err()
        );
    }

    #[tokio::test(start_paused = true)]
    async fn queue_admission_is_bounded() {
        let limiter = LocalRateLimiter::per_second(1, 0);
        limiter.acquire().await.unwrap();
        let waiting = {
            let limiter = limiter.clone();
            tokio::spawn(async move { limiter.acquire().await })
        };
        tokio::task::yield_now().await;
        assert!(limiter.acquire().await.is_err());
        waiting.abort();
    }
}
