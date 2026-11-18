// Usage: ocr IMAGE [IMAGE ...]   Prints one JSON object per image: {"file": ..., "lines": [...]}.
// Fast recognition, no language correction: the scripts only look for fixed strings.
import Foundation
import Vision

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
    try? handler.perform([request])
    let out: [String: Any] = ["file": path, "lines": lines]
    let data = try! JSONSerialization.data(withJSONObject: out)
    print(String(data: data, encoding: .utf8)!)
}
