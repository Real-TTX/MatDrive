/* MatDrive – a select with data-autosubmit sends its form when it changes (the button beside it stays for people without script). */
(function () {
  'use strict';
  document.querySelectorAll('[data-autosubmit]').forEach(function (select) {
    var button = select.form && select.form.querySelector('button[type="submit"]');
    if (button) { button.hidden = true; }
    select.addEventListener('change', function () { select.form.requestSubmit(); });
  });
})();
