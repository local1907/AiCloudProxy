/* AI Cloud Proxy — marketing site: small progressive enhancements only.
   The page works fully without JavaScript. */
(function () {
  'use strict';

  /* ---------- Mobile menu ---------- */
  var navToggle = document.getElementById('navToggle');
  if (navToggle) {
    navToggle.addEventListener('click', function () {
      var open = document.body.classList.toggle('nav-open');
      navToggle.setAttribute('aria-expanded', String(open));
    });

    // Close the menu when a link inside it is chosen.
    document.querySelectorAll('.mobile-menu a').forEach(function (link) {
      link.addEventListener('click', function () {
        document.body.classList.remove('nav-open');
        navToggle.setAttribute('aria-expanded', 'false');
      });
    });
  }

  /* ---------- Reveal on scroll ---------- */
  var revealEls = document.querySelectorAll('.reveal');
  if ('IntersectionObserver' in window && revealEls.length) {
    var io = new IntersectionObserver(
      function (entries) {
        entries.forEach(function (entry) {
          if (entry.isIntersecting) {
            entry.target.classList.add('visible');
            io.unobserve(entry.target);
          }
        });
      },
      { threshold: 0.12, rootMargin: '0px 0px -40px 0px' }
    );
    revealEls.forEach(function (el) { io.observe(el); });
  } else {
    revealEls.forEach(function (el) { el.classList.add('visible'); });
  }

  /* ---------- Smooth-scroll offset for the sticky header ---------- */
  var header = document.querySelector('.site-header');
  var headerOffset = header ? header.offsetHeight : 0;
  document.querySelectorAll('a[href^="#"]').forEach(function (anchor) {
    anchor.addEventListener('click', function (e) {
      var id = anchor.getAttribute('href');
      if (id && id.length > 1) {
        var target = document.querySelector(id);
        if (target && window.scrollY !== undefined) {
          e.preventDefault();
          var top = target.getBoundingClientRect().top + window.pageYOffset - headerOffset - 10;
          window.scrollTo({ top: Math.max(top, 0), behavior: 'smooth' });
        }
      }
    });
  });
})();
