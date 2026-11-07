/* MatDrive – on every page: the menu "New". In a folder the explorer (explorer.js) does what was chosen; on any other page the choice opens My Drive and is done
   there (?new=folder|files|dir|office:docs …). */
(function () {
  'use strict';

  document.addEventListener('click', function (e) {
    var item = e.target.closest && e.target.closest('[data-new]');
    if (!item) { return; }
    var what = item.getAttribute('data-new');
    if (window.mdExplorer && window.mdExplorer.canAdd) {
      e.preventDefault();
      window.mdExplorer.create(what);
      return;
    }
    location.assign('/drive?new=' + encodeURIComponent(what));
  });
})();
