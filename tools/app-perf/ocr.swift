// Usage: ocr IMAGE [IMAGE ...]   Prints one JSON object per image: {"file": ..., "lines": [...]}.
// Fast recognition, no language correction: the scripts only look for fixed strings.
// An image that cannot be read prints nothing and makes the exit status 1, so a caller never caches it as blank.
import Foundation
import Vision

var failed = false
for path in CommandLine.arguments.dropFirst() {
    var lines: [String] = []
    let request = VNRecognizeTextRequest { req, _ in
        for case let obs as VNRecognizedTextObservation in req.results ?? [] {
            if let top = obs.topCandidates(1).first { lines.append(top.string) }
        }
    }
    request.recognitionLevel = .fast
    request.usesLanguageCorrection = false
    let handler = VNImageRequestHandler(url: URL(fileURLWithPath: path))
    do {
        try handler.perform([request])
    } catch {
        FileHandle.standardError.write("\(path): \(error)\n".data(using: .utf8)!)
        failed = true
        continue
    }
    let out: [String: Any] = ["file": path, "lines": lines]
    let data = try! JSONSerialization.data(withJSONObject: out)
    print(String(data: data, encoding: .utf8)!)
}
exit(failed ? 1 : 0)
