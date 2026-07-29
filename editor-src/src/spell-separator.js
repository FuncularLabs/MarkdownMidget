// Characters the spell checker treats as a word break, shared by the repeated-word
// deletion. Kept in its own module so it can be tested: written with explicit
// escapes because literal zero-width characters here would silently degrade to
// /[\s-]/ (making '-' a separator) if any tool stripped them.
// Mirrors MainWindow.IsSeparator on the host side.
export const SEPARATOR = /[\s\u00AD\u200B-\u200D\uFEFF]/;
