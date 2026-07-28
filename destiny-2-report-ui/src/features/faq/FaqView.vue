<script setup lang="ts">
const questions = [
  {
    question: 'Why can report generation take so long?',
    answer:
      "Building a report means retrieving the available post-game carnage report for every activity in a player's Destiny 2 history. Veteran accounts can have thousands of activities, and Bungie reasonably rate-limits how quickly those records can be requested. The crawler processes them as efficiently as the API allows, but a complete first report may still take some time.",
  },
  {
    question: "Why don't some report statistics match my in-game stat trackers?",
    answer:
      "There is not always a clear one-to-one relationship between Bungie's in-game trackers and the activity data exposed through its API. Different trackers may use different definitions, filters, or historical windows. The report calculates its totals from the completed activities and statistics available to the site, and they are made as accurate and consistent as that source data permits.",
  },
  {
    question: "What's the progress bar on the home page?",
    answer:
      'Community leaderboards remain hidden while the site builds a representative pool of reports. They will appear automatically after 10,000 public Guardian reports have completed their first crawl, and each leaderboard can include the top 1,000 players for the statistics it tracks.',
  },
  {
    question: 'How often is my report updated?',
    answer:
      'Opening an existing report asks the site to check for newer activity automatically. You can also use the refresh control on the report. To protect the service and stay within Bungie API limits, a report that was crawled recently may need to wait before another refresh can begin.',
  },
  {
    question: 'Why can’t a report be generated for a particular Guardian?',
    answer:
      'The account may have its activity history hidden through Bungie privacy settings. The player can enable “Show my Destiny game Activity feed on Bungie.net” under Bungie.net Settings, then try generating the report again.',
  },
  {
    question: 'Do I need to sign in with Bungie?',
    answer:
      'No. You can search for a Bungie name and view any available public report without signing in. Bungie sign-in is used for account-specific features such as opening your own report directly and viewing Your Story.',
  },
]
</script>

<template>
  <div class="faq container">
    <header class="faq-header">
      <h1 class="display">Frequently asked questions</h1>
      <p>
        A few details about how Destiny 2 Report gathers data, builds reports, and keeps the
        community rankings fair.
      </p>
    </header>

    <section class="question-list" aria-label="Frequently asked questions">
      <article v-for="item in questions" :key="item.question" class="question">
        <h2>{{ item.question }}</h2>
        <p>{{ item.answer }}</p>
      </article>

      <article class="question">
        <h2>Where can I report a bug or request a feature?</h2>
        <p>
          Send a message or tag
          <a href="https://x.com/Chopstickleg">@Chopstickleg on X</a>, or
          <a href="https://github.com/ChopstickLeg/Destiny2Report/issues">open an issue on GitHub</a
          >. Destiny 2 Report is open source, so contributions are welcome too.
        </p>
      </article>
    </section>
  </div>
</template>

<style scoped>
.faq {
  padding-top: var(--space-7);
}

.faq-header {
  padding-bottom: var(--space-6);
  border-bottom: 1px solid var(--color-border);
}

.faq-header h1 {
  font-size: clamp(var(--text-xl), 4vw, var(--text-2xl));
}

.faq-header > p:last-child {
  max-width: 42rem;
  margin-top: var(--space-3);
  color: var(--color-text-secondary);
}

.question-list {
  display: grid;
}

.question {
  display: grid;
  grid-template-columns: minmax(12rem, 0.8fr) minmax(0, 1.4fr);
  gap: var(--space-6);
  padding-block: var(--space-5);
  border-bottom: 1px solid var(--color-border);
}

.question h2 {
  font-size: var(--text-md);
}

.question p {
  color: var(--color-text-secondary);
}

.question a {
  color: var(--color-accent-strong);
  text-decoration: underline;
  text-decoration-color: var(--color-border-strong);
  text-underline-offset: 0.2em;
}

@media (max-width: 40rem) {
  .question {
    grid-template-columns: 1fr;
    gap: var(--space-2);
  }
}
</style>
