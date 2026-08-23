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

guard CommandLine.arguments.count >= 2 else {
    FileHandle.standardError.write(Data("usage: vision-ocr image-path [jpn] [eng]\n".utf8))
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

let requested = CommandLine.arguments.dropFirst(2).compactMap { language -> String? in
    switch language.lowercased() {
    case "jpn", "ja", "ja-jp": return "ja-JP"
    case "eng", "en", "en-us": return "en-US"
    default: return nil
    }
}
var seenLanguages = Set<String>()
let orderedLanguages = requested.filter { seenLanguages.insert($0).inserted }
let request = VNRecognizeTextRequest()
request.recognitionLevel = .accurate
request.recognitionLanguages = orderedLanguages.isEmpty ? ["ja-JP", "en-US"] : orderedLanguages
request.usesLanguageCorrection = true

let handler = VNImageRequestHandler(cgImage: cgImage, options: [:])
try handler.perform([request])
let lines = (request.results ?? []).compactMap { observation -> OcrLine? in
    guard let candidate = observation.topCandidates(1).first else { return nil }
    let box = observation.boundingBox
    return OcrLine(text: candidate.string, confidence: Double(candidate.confidence), x: box.origin.x,
                   y: box.origin.y, width: box.size.width, height: box.size.height)
}
FileHandle.standardOutput.write(try JSONEncoder().encode(lines))
