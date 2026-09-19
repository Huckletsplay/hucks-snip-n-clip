import Foundation

/// One global naming rule for future Snips and Clips. The default renders the exact filename stem
/// used before templates existed, so installing the feature changes no names until the user asks.
struct CaptureFilenameConfiguration: Equatable {
    static let defaultTemplate = "{label}_{kind}_{date}_{hour}-{minute}-{second}-{milliseconds}"
    static let fallbackLabel = "HucksSnipNClip"

    var label = ""
    var template = CaptureFilenameConfiguration.defaultTemplate
    var nextCounter = 1

    var isDefault: Bool {
        label.isEmpty && template == CaptureFilenameConfiguration.defaultTemplate
    }
}

enum CaptureFilenameKind: String {
    case snip = "Snip"
    case clip = "Clip"
}

enum CaptureFilenameTemplate {
    static let supportedPlaceholders = [
        "label", "kind", "date", "hour", "minute", "second", "milliseconds", "counter"
    ]

    /// Normalizes only choices whose meaning is explicit: blank labels use the product fallback,
    /// blank templates mean the default, and counters begin at one. It does not silently repair an
    /// unsafe filename; those are rejected with a useful message.
    static func normalized(_ configuration: CaptureFilenameConfiguration) throws
        -> CaptureFilenameConfiguration {
        var result = configuration
        result.label = result.label.trimmingCharacters(in: .whitespacesAndNewlines)
        if result.template.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            result.template = CaptureFilenameConfiguration.defaultTemplate
        }
        result.nextCounter = max(1, result.nextCounter)

        _ = try stem(
            configuration: result,
            kind: .snip,
            capturedAt: Date(timeIntervalSince1970: 1_788_800_496.789),
            counter: result.nextCounter,
            timeZone: TimeZone(secondsFromGMT: 0)!)
        _ = try stem(
            configuration: result,
            kind: .clip,
            capturedAt: Date(timeIntervalSince1970: 1_788_800_496.789),
            counter: result.nextCounter,
            timeZone: TimeZone(secondsFromGMT: 0)!)
        return result
    }

    static func stem(
        configuration: CaptureFilenameConfiguration,
        kind: CaptureFilenameKind,
        capturedAt: Date,
        counter: Int,
        timeZone: TimeZone = .current
    ) throws -> String {
        var calendar = Calendar(identifier: .gregorian)
        calendar.locale = Locale(identifier: "en_US_POSIX")
        calendar.timeZone = timeZone
        let components = calendar.dateComponents(
            [.year, .month, .day, .hour, .minute, .second, .nanosecond],
            from: capturedAt)
        let milliseconds = max(0, min(999, (components.nanosecond ?? 0) / 1_000_000))
        let label = configuration.label.trimmingCharacters(in: .whitespacesAndNewlines)
        let values = [
            "label": label.isEmpty ? CaptureFilenameConfiguration.fallbackLabel : label,
            "kind": kind.rawValue,
            "date": String(format: "%04d-%02d-%02d",
                           components.year ?? 0, components.month ?? 0, components.day ?? 0),
            "hour": String(format: "%02d", components.hour ?? 0),
            "minute": String(format: "%02d", components.minute ?? 0),
            "second": String(format: "%02d", components.second ?? 0),
            "milliseconds": String(format: "%03d", milliseconds),
            "counter": String(format: "%03d", max(1, counter)),
        ]

        let template = configuration.template.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
            ? CaptureFilenameConfiguration.defaultTemplate
            : configuration.template
        var rendered = ""
        var index = template.startIndex
        while index < template.endIndex {
            let character = template[index]
            if character == "{" {
                guard let close = template[index...].firstIndex(of: "}") else {
                    throw CaptureFilenameTemplateError.unmatchedOpeningBrace
                }
                let tokenStart = template.index(after: index)
                let token = String(template[tokenStart..<close])
                guard let value = values[token] else {
                    throw CaptureFilenameTemplateError.unsupportedPlaceholder(token)
                }
                rendered += value
                index = template.index(after: close)
            } else if character == "}" {
                throw CaptureFilenameTemplateError.unmatchedClosingBrace
            } else {
                rendered.append(character)
                index = template.index(after: index)
            }
        }

        try validateRenderedStem(rendered)
        return rendered
    }

    private static func validateRenderedStem(_ stem: String) throws {
        guard !stem.isEmpty else { throw CaptureFilenameTemplateError.emptyFilename }
        guard !stem.hasPrefix(".") else { throw CaptureFilenameTemplateError.hiddenFilename }
        guard !stem.hasSuffix(" "), !stem.hasSuffix(".") else {
            throw CaptureFilenameTemplateError.trailingSpaceOrPeriod
        }

        let forbidden = CharacterSet(charactersIn: "<>:\"/\\|?*")
        if stem.unicodeScalars.contains(where: {
            forbidden.contains($0) || $0.value < 32 || $0.value == 127
        }) {
            throw CaptureFilenameTemplateError.invalidCharacter
        }

        let deviceName = stem.split(separator: ".", maxSplits: 1, omittingEmptySubsequences: false)
            .first.map(String.init)?.uppercased() ?? ""
        let reserved = Set(["CON", "PRN", "AUX", "NUL"]
            + (1...9).flatMap { ["COM\($0)", "LPT\($0)"] })
        if reserved.contains(deviceName) {
            throw CaptureFilenameTemplateError.reservedName(stem)
        }

        // Leave room under the common 255-byte filename limit for a collision suffix and extension.
        guard stem.lengthOfBytes(using: .utf8) <= 220 else {
            throw CaptureFilenameTemplateError.filenameTooLong
        }
    }
}

enum CaptureFilenameTemplateError: LocalizedError, Equatable {
    case unmatchedOpeningBrace
    case unmatchedClosingBrace
    case unsupportedPlaceholder(String)
    case emptyFilename
    case hiddenFilename
    case trailingSpaceOrPeriod
    case invalidCharacter
    case reservedName(String)
    case filenameTooLong

    var errorDescription: String? {
        switch self {
        case .unmatchedOpeningBrace:
            return "A { placeholder is missing its closing } brace."
        case .unmatchedClosingBrace:
            return "A } brace does not have a matching { placeholder."
        case .unsupportedPlaceholder(let name):
            let shown = name.isEmpty ? "{}" : "{\(name)}"
            return "\(shown) is not supported. Use: "
                + CaptureFilenameTemplate.supportedPlaceholders.map { "{\($0)}" }.joined(separator: ", ")
        case .emptyFilename:
            return "The template produces an empty filename."
        case .hiddenFilename:
            return "The filename cannot begin with a period because macOS would hide it."
        case .trailingSpaceOrPeriod:
            return "The filename cannot end with a space or period."
        case .invalidCharacter:
            return "Filenames cannot contain < > : \" / \\ | ? *, line breaks, or control characters."
        case .reservedName(let name):
            return "\(name) is reserved by Windows and cannot be used as a cross-platform filename."
        case .filenameTooLong:
            return "The filename is too long. Shorten the label or template."
        }
    }
}
