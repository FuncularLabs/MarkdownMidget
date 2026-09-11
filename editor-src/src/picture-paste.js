// The picture ceiling, applied to a paste into the formatted view.
//
// A picture goes into the document as a base64 data URI, and the host refuses one
// larger than its ceiling (PictureLimit on the host) on every route it can see: a
// dropped file and Insert ▸ Picture. A paste here is the route it can't see. With
// no upload plugin, a clipboard holding only a picture reaches ProseMirror as a
// paste with no text; ProseMirror hands it to Chromium's native paste (its
// capturePaste, a hidden contenteditable), and then picks the <img src="data:…">
// Chromium made up from there. So the ceiling is applied here, before any of that:
// editor-factory.js asks refusedPictureSize from a paste handler ProseMirror runs
// ahead of its own, and cancels the paste when it answers.
//
// The ceiling is not a number written here. The host hands it to MDM.create
// (PictureLimit.EditorOptionsJson), so there is one ceiling — the host's — and
// moving it moves both sides.
//
// Pure: no DOM, no editor. Each function takes plain objects shaped like the
// browser's, so the whole decision is tested under node (picture-paste.test.mjs).

/** The ceiling the host passed to MDM.create, or null when it passed none. */
export function ceilingFrom(options) {
  const value = options ? options.maxPictureBytes : undefined;
  return Number.isSafeInteger(value) && value >= 0 ? value : null;
}

/** The web message that tells the host a paste was refused, and for how big a
 *  picture. The host answers it with the status-bar notice its other routes use. */
export function refusalMessage(size) {
  return { type: 'pictureRefused', size };
}

const isPicture = (type) => typeof type === 'string' && type.startsWith('image/');

// Whether ProseMirror will paste TEXT from this clipboard: the same four reads its
// paste handler makes (getText and the HTML read, in prosemirror-view). When any
// of them is non-empty it pastes that, and a picture beside it is never used — so
// such a paste is not this guard's business. It is the source view's rule too
// (ImagePaste.ShouldHandle): text wins.
function carriesText(data) {
  return !!(data.getData('text/plain') || data.getData('Text')
    || data.getData('text/uri-list') || data.getData('text/html'));
}

// The picture files on the clipboard: from `items` where the browser lists them
// (Chromium does), from `files` otherwise. Only a picture item is asked for its
// file — there is nothing to learn from any other.
function pictures(data) {
  if (data.items) {
    return Array.from(data.items)
      .filter((item) => item && item.kind === 'file' && isPicture(item.type))
      .map((item) => item.getAsFile());
  }
  return Array.from(data.files || []).filter((file) => file && isPicture(file.type));
}

/**
 * The size of the picture a paste must be refused for, or null when the paste is
 * none of the ceiling's business and goes ahead untouched.
 *
 * Refused: a clipboard with no text and at least one picture larger than
 * `ceiling` bytes — exactly the ceiling goes in, one byte more does not, the host's
 * boundary (PictureLimit.IsTooLarge). With several pictures the largest is
 * reported; one over is enough to refuse the paste, which is one action.
 *
 * Untouched: no clipboardData; no usable ceiling (the host always passes one — a
 * page started without it pastes as it always did); a clipboard carrying text; no
 * picture on it; and a picture whose size can't be read, which is the host's "did
 * not say" (-1), past no ceiling.
 */
export function refusedPictureSize(clipboardData, ceiling) {
  if (!clipboardData || !Number.isSafeInteger(ceiling) || ceiling < 0) return null;
  if (carriesText(clipboardData)) return null;
  let largest = null;
  for (const file of pictures(clipboardData)) {
    const size = file && Number.isFinite(file.size) ? file.size : -1;
    if (size > ceiling && (largest === null || size > largest)) largest = size;
  }
  return largest;
}
