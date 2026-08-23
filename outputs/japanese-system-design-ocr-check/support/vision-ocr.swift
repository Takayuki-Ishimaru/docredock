import AppKit
import Foundation
import Vision

struct OcrLine: Codable {
    let text: String
    let confidence: Double
    let x: Double
    let y: Double
    let width: Double
    let height: Double
}

guard CommandLine.arguments.count == 2 else {
    FileHandle.standardError.write(Data("usage: vision-ocr image-path\n".utf8))
    exit(2)
}

let imageUrl = URL(fileURLWithPath: CommandLine.arguments[1])
guard let image = NSImage(contentsOf: imageUrl) else {
    FileHandle.standardError.write(Data("image could not be opened\n".utf8))
    exit(3)
}
var proposed = NSRect(origin: .zero, size: image.size)
guard let cgImage = image.cgImage(forProposedRect: &proposed, context: nil, hints: nil) else {
    FileHandle.standardError.write(Data("image could not be converted\n".utf8))
    exit(4)
}

let request = VNRecognizeTextRequest()
request.recognitionLevel = .accurate
request.recognitionLanguages = ["ja-JP", "en-US"]
request.usesLanguageCorrection = true

let handler = VNImageRequestHandler(cgImage: cgImage, options: [:])
try handler.perform([request])

let lines = (request.results ?? []).compactMap { observation -> OcrLine? in
    guard let candidate = observation.topCandidates(1).first else { return nil }
    let box = observation.boundingBox
    return OcrLine(
        text: candidate.string,
        confidence: Double(candidate.confidence),
        x: box.origin.x,
        y: box.origin.y,
        width: box.size.width,
        height: box.size.height
    )
}

let data = try JSONEncoder().encode(lines)
FileHandle.standardOutput.write(data)
