'use strict';
(() => {
  const key = 'steppewise-analytics-v1';
  const lifetime = 180 * 24 * 60 * 60 * 1000;
  let choice = null, loaded = false;
  try {
    const saved = JSON.parse(localStorage.getItem(key));
    if (saved && Date.now() - saved.time < lifetime && ['accepted', 'declined'].includes(saved.choice)) choice = saved.choice;
  } catch { /* An unavailable preference store must not prevent reading the page. */ }

  const panel = document.createElement('section');
  panel.className = 'analytics-choice';
  panel.setAttribute('aria-labelledby', 'analytics-title');
  panel.innerHTML = '<h2 id="analytics-title">Optional analytics</h2><p>With your permission, Microsoft Clarity uses cookies and records interactions on this walkthrough to help improve it through heatmaps and session replay. Data is sent to Microsoft. Advertising storage is disabled. You can decline and use the full walkthrough, or change your choice in the footer. Your preference is saved in this browser for 180 days when browser storage is available. Turning analytics off reloads the walkthrough and resets the simulation.</p><p><a href="https://privacy.microsoft.com/privacystatement">Microsoft Privacy Statement</a></p>';
  const status = document.createElement('p');
  status.setAttribute('role', 'status');
  const accept = document.createElement('button'), decline = document.createElement('button');
  accept.type = decline.type = 'button';
  accept.textContent = 'Allow analytics';
  decline.textContent = 'Decline analytics';
  panel.append(accept, decline, status);
  document.querySelector('header').after(panel);

  const settings = document.createElement('button');
  settings.type = 'button';
  settings.textContent = 'Analytics preferences';
  settings.className = 'analytics-settings';
  settings.onclick = () => { status.textContent = choice === 'accepted' ? 'Analytics currently allowed.' : 'Analytics currently off.'; panel.hidden = false; accept.focus(); };
  document.querySelector('footer').append(settings);

  function load() {
    if (loaded) return;
    loaded = true;
    window.clarity = window.clarity || function () { (window.clarity.q = window.clarity.q || []).push(arguments); };
    window.clarity('consentv2', { analytics_Storage: 'granted', ad_Storage: 'denied' });
    const script = document.createElement('script');
    script.async = true;
    script.src = 'https://www.clarity.ms/tag/z7grjrdsol';
    document.head.append(script);
  }
  function choose(value) {
    choice = value;
    try { localStorage.setItem(key, JSON.stringify({ choice, time: Date.now() })); } catch { /* Choice applies to this visit. */ }
    if (choice === 'accepted') load();
    else if (loaded) {
      window.clarity('consentv2', { analytics_Storage: 'denied', ad_Storage: 'denied' });
      // Unload the recorder as well as denying cookie consent.
      location.reload();
      return;
    }
    panel.hidden = true;
    const heading = document.querySelector('h1');
    heading.tabIndex = -1;
    heading.focus();
  }
  accept.onclick = () => choose('accepted');
  decline.onclick = () => choose('declined');
  panel.hidden = choice !== null;
  status.textContent = choice === 'accepted' ? 'Analytics currently allowed.' : 'Analytics currently off.';
  if (choice === 'accepted') load();
})();
