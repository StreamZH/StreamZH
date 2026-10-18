// Theme before the first paint (no flash): «light» or «dark» from the last choice, otherwise the system decides.
try {
  var t = localStorage.getItem('streamzh-theme')
  if (t === 'dark' || t === 'light') document.documentElement.dataset.theme = t
} catch (e) {
  // no storage
}
