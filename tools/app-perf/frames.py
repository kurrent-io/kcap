"""Shared by the report scripts: OCR a run's frames and find the first one containing a string."""
import json
import os
import re
import subprocess
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))


def ocr_frames(frames_dir):
    """[(capture_start, capture_end, text)] in time order, cached beside the frames as ocr.jsonl. A frame with no
    entry in the times file ends at the next frame's start, which the loop takes after the capture returns; the last
    such frame has no known end and gets None."""
    cache = os.path.join(frames_dir, "ocr.jsonl")
    files = sorted(f for f in os.listdir(frames_dir) if f.endswith(".jpg"))
    if not os.path.exists(cache):
        paths = [os.path.join(frames_dir, f) for f in files]
        fd, partial = tempfile.mkstemp(dir=frames_dir, prefix="ocr.", suffix=".partial")
        try:
            with os.fdopen(fd, "w") as out:
                for i in range(0, len(paths), 40):
                    out.write(subprocess.run([os.path.join(HERE, "ocr"), *paths[i:i + 40]], check=True,
                                             capture_output=True, text=True).stdout)
            os.replace(partial, cache)
        except BaseException:
            if os.path.exists(partial):
                os.remove(partial)
            raise
    ends = {}
    times = os.path.join(frames_dir, "times")
    if os.path.exists(times):
        for line in open(times):
            name, done_at = line.split()
            ends[name] = float(done_at)
    rows = [json.loads(line) for line in open(cache) if line.startswith("{")]
    frames = []
    for row in rows:
        name = os.path.basename(row["file"])[:-4]
        start = float(name)
        frames.append((start, ends.get(name), "\n".join(row["lines"])))
    frames.sort()
    return [(start, end if end is not None else (frames[i + 1][0] if i + 1 < len(frames) else None), text)
            for i, (start, end, text) in enumerate(frames)]


def first_seen(frames, needle):
    """(found, end of the first frame showing needle or None if unknown, start of the frame before it): the event fell
    between the last two.
    A needle starting with re: is a case-insensitive regular expression; OCR misreads punctuation, so anchor on
    digits and letters."""
    match = (re.compile(needle[3:], re.I).search if needle.startswith("re:")
             else lambda text: needle.lower() in text.lower())
    previous = None
    for start, end, text in frames:
        if match(text):
            return True, end, previous
        previous = start
    return False, None, None
