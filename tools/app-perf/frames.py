"""Shared by the report scripts: OCR a run's frames and find the first one containing a string."""
import json
import os
import re
import subprocess

HERE = os.path.dirname(os.path.abspath(__file__))


def ocr_frames(frames_dir):
    """[(capture_start, capture_end, text)] in time order, cached beside the frames as ocr.jsonl. A frame with no
    entry in the times file gets its start as its end."""
    cache = os.path.join(frames_dir, "ocr.jsonl")
    files = sorted(f for f in os.listdir(frames_dir) if f.endswith(".jpg"))
    if not os.path.exists(cache):
        paths = [os.path.join(frames_dir, f) for f in files]
        partial = cache + ".partial"
        try:
            with open(partial, "w") as out:
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
        frames.append((start, ends.get(name, start), "\n".join(row["lines"])))
    frames.sort()
    return frames


def first_seen(frames, needle):
    """(end of the first frame showing needle, start of the frame before it): the event fell between the two.
    A needle starting with re: is a case-insensitive regular expression; OCR misreads punctuation, so anchor on
    digits and letters."""
    match = (re.compile(needle[3:], re.I).search if needle.startswith("re:")
             else lambda text: needle.lower() in text.lower())
    previous = None
    for start, end, text in frames:
        if match(text):
            return end, previous
        previous = start
    return None, None
