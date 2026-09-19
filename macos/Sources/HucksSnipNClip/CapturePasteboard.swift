import AppKit

/// The paste-first accompaniment to routing. A capture is saved to its Output first; only then is
/// it also put on the pasteboard - a Snip as image data, a finished Clip as its file - so Command-V
/// works straight away. Routing stays primary: these return false on failure and never throw, so a
/// pasteboard problem can warn but can never turn a saved capture into a reported failure.
enum CapturePasteboard {
    /// Reads back the PNG that was actually saved, so what pastes is byte-for-byte the routed file.
    /// TIFF is added for applications that do not read PNG from the pasteboard.
    @discardableResult
    static func copySnip(atPath path: String, to pasteboard: NSPasteboard = .general) -> Bool {
        guard let png = try? Data(contentsOf: URL(fileURLWithPath: path)),
              let image = NSImage(data: png),
              let tiff = image.tiffRepresentation else { return false }

        let item = NSPasteboardItem()
        guard item.setData(png, forType: .png), item.setData(tiff, forType: .tiff) else { return false }
        pasteboard.clearContents()
        return pasteboard.writeObjects([item])
    }

    /// A clip is too large to be pasteboard data, so the finished file itself is offered, the way
    /// Finder's Copy does it. Applications that accept file pastes or attachments take it from here.
    @discardableResult
    static func copyClip(atPath path: String, to pasteboard: NSPasteboard = .general) -> Bool {
        guard FileManager.default.fileExists(atPath: path) else { return false }
        pasteboard.clearContents()
        return pasteboard.writeObjects([URL(fileURLWithPath: path) as NSURL])
    }
}
