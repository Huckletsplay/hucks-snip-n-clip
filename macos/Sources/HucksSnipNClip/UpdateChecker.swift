import CryptoKit
import Foundation

/// `<major>.<minor>.<patch>`, optionally written `v<major>.<minor>.<patch>` as the release tags are.
/// Anything else is refused rather than guessed at, so a malformed tag can never look like an update.
struct SemanticVersion: Comparable, CustomStringConvertible {
    let major: Int
    let minor: Int
    let patch: Int

    init?(_ text: String) {
        var value = text.trimmingCharacters(in: .whitespacesAndNewlines)
        if value.first == "v" || value.first == "V" { value.removeFirst() }
        let parts = value.split(separator: ".", omittingEmptySubsequences: false)
        guard parts.count == 3 else { return nil }
        var numbers: [Int] = []
        for part in parts {
            guard !part.isEmpty, part.count <= 6, part.allSatisfy({ $0.isASCII && $0.isNumber }),
                  let number = Int(part) else { return nil }
            numbers.append(number)
        }
        major = numbers[0]
        minor = numbers[1]
        patch = numbers[2]
    }

    var description: String { "\(major).\(minor).\(patch)" }

    static func < (lhs: SemanticVersion, rhs: SemanticVersion) -> Bool {
        return (lhs.major, lhs.minor, lhs.patch) < (rhs.major, rhs.minor, rhs.patch)
    }
}

/// The one place the Mac release asset names are defined. The updater accepts only this exact
/// pair; a future signed or notarized channel must be added here deliberately, never matched loosely.
enum MacReleaseAssets {
    static func dmgName(for version: SemanticVersion) -> String {
        return "HucksSnipNClip-\(version)-macOS-universal-unsigned-beta.dmg"
    }

    /// `release.sh` writes `shasum -a 256` output beside the DMG under this name.
    static func checksumName(for version: SemanticVersion) -> String {
        return dmgName(for: version) + ".sha256.txt"
    }
}

struct GitHubReleaseAsset: Decodable, Equatable {
    let name: String
    let browserDownloadURL: URL
    let size: Int?

    enum CodingKeys: String, CodingKey {
        case name
        case browserDownloadURL = "browser_download_url"
        case size
    }
}

struct GitHubRelease: Decodable {
    let tagName: String
    let htmlURL: URL?
    let assets: [GitHubReleaseAsset]

    enum CodingKeys: String, CodingKey {
        case tagName = "tag_name"
        case htmlURL = "html_url"
        case assets
    }
}

struct UpdateOffer: Equatable {
    let installed: SemanticVersion
    let available: SemanticVersion
    let dmg: GitHubReleaseAsset
    let checksum: GitHubReleaseAsset
    let releasePage: URL?
}

enum UpdateCheckOutcome: Equatable {
    /// The installed copy is the latest release, or newer than it.
    case upToDate(installed: SemanticVersion, latest: SemanticVersion)
    /// A newer version exists, but without the exact verified Mac DMG pair.
    case newerWithoutMacDownload(installed: SemanticVersion, latest: SemanticVersion, releasePage: URL?)
    case available(UpdateOffer)
}

enum UpdateError: LocalizedError, Equatable {
    case installedVersionUnknown(String)
    case network(String)
    case httpStatus(Int)
    case unreadableRelease
    case malformedReleaseVersion(String)
    case insecureURL(String)
    case missingChecksum
    case malformedChecksum
    case checksumNamesDifferentFile(expected: String, found: String)
    case incompleteDownload(expected: Int, received: Int)
    case digestMismatch

    /// A failure that means the download could not be trusted, as opposed to one that merely
    /// could not complete. These are presented as security stops.
    var isSecurityFailure: Bool {
        switch self {
        case .insecureURL, .missingChecksum, .malformedChecksum, .checksumNamesDifferentFile,
             .incompleteDownload, .digestMismatch:
            return true
        default:
            return false
        }
    }

    var errorDescription: String? {
        switch self {
        case .installedVersionUnknown(let value):
            return "This copy's version could not be read (\(value)), so it cannot be compared with a release."
        case .network(let detail):
            return "GitHub could not be reached. Check the internet connection and try again.\n\n\(detail)"
        case .httpStatus(let status):
            return "GitHub answered with HTTP status \(status). Try again later."
        case .unreadableRelease:
            return "GitHub returned a release description that could not be read."
        case .malformedReleaseVersion(let tag):
            return "The latest release has a version this app does not recognize (\(tag)), so it was not offered as an update."
        case .insecureURL(let url):
            return "The download was refused because it was not served securely from GitHub:\n\(url)"
        case .missingChecksum:
            return "The published checksum could not be downloaded, so the update was not opened."
        case .malformedChecksum:
            return "The published checksum is not a single valid SHA-256 value, so the update was not opened."
        case .checksumNamesDifferentFile(let expected, let found):
            return "The published checksum describes \(found) instead of \(expected), so the update was not opened."
        case .incompleteDownload(let expected, let received):
            return "The download was incomplete (\(received) of \(expected) bytes), so the update was not opened."
        case .digestMismatch:
            return "The downloaded DMG does not match its published SHA-256 checksum. It was deleted and not opened."
        }
    }
}

/// The network surface the updater needs. The real one is URLSession; tests supply fixtures.
protocol UpdateHTTPClient: Sendable {
    /// Returns the body, the final URL after redirects, and the HTTP status.
    func data(from url: URL) async throws -> (Data, URL?, Int)
    /// Downloads to a temporary file the caller then owns.
    func download(from url: URL) async throws -> (URL, URL?, Int)
}

struct URLSessionUpdateClient: UpdateHTTPClient {
    let userAgent: String

    private func request(_ url: URL) -> URLRequest {
        var request = URLRequest(url: url, cachePolicy: .reloadIgnoringLocalCacheData, timeoutInterval: 60)
        request.setValue(userAgent, forHTTPHeaderField: "User-Agent")
        if url.host?.lowercased() == "api.github.com" {
            request.setValue("application/vnd.github+json", forHTTPHeaderField: "Accept")
        }
        return request
    }

    func data(from url: URL) async throws -> (Data, URL?, Int) {
        let (data, response) = try await URLSession.shared.data(for: request(url))
        return (data, response.url, (response as? HTTPURLResponse)?.statusCode ?? 0)
    }

    func download(from url: URL) async throws -> (URL, URL?, Int) {
        let (file, response) = try await URLSession.shared.download(for: request(url))
        // URLSession deletes its file when this returns; move it somewhere the caller owns.
        let kept = FileManager.default.temporaryDirectory
            .appendingPathComponent("HucksSnipNClip-download-\(UUID().uuidString)")
        try FileManager.default.moveItem(at: file, to: kept)
        return (kept, response.url, (response as? HTTPURLResponse)?.statusCode ?? 0)
    }
}

/// Explicit, user-requested update discovery and download verification. It never contacts GitHub
/// on its own, never opens or mounts anything, and never touches settings or captures.
struct UpdateService: Sendable {
    static let latestReleaseURL = URL(string: "https://api.github.com/repos/Huckletsplay/hucks-snip-n-clip/releases/latest")!
    static let maximumChecksumBytes = 4096

    let client: UpdateHTTPClient
    let workDirectory: URL

    static func defaultWorkDirectory() -> URL {
        // The per-user private temporary folder - never an Output or the project tree.
        return FileManager.default.temporaryDirectory.appendingPathComponent("HucksSnipNClip-Update", isDirectory: true)
    }

    func check(installedVersion text: String) async throws -> UpdateCheckOutcome {
        guard let installed = SemanticVersion(text) else { throw UpdateError.installedVersionUnknown(text) }

        let (data, finalURL, status) = try await fetch(Self.latestReleaseURL)
        try Self.requireSecure(finalURL ?? Self.latestReleaseURL)
        guard status == 200 else { throw UpdateError.httpStatus(status) }
        guard let release = try? JSONDecoder().decode(GitHubRelease.self, from: data) else {
            throw UpdateError.unreadableRelease
        }
        guard let latest = SemanticVersion(release.tagName) else {
            throw UpdateError.malformedReleaseVersion(release.tagName)
        }
        guard latest > installed else { return .upToDate(installed: installed, latest: latest) }
        guard let pair = Self.selectMacAssets(from: release.assets, version: latest) else {
            return .newerWithoutMacDownload(installed: installed, latest: latest, releasePage: release.htmlURL)
        }
        return .available(UpdateOffer(
            installed: installed,
            available: latest,
            dmg: pair.dmg,
            checksum: pair.checksum,
            releasePage: release.htmlURL))
    }

    /// Exactly the named DMG and its exact checksum, both served from github.com over HTTPS. A
    /// Windows installer, a source archive, an ad-hoc build, a near-miss name, or a DMG without
    /// its checksum all yield nil.
    static func selectMacAssets(
        from assets: [GitHubReleaseAsset],
        version: SemanticVersion
    ) -> (dmg: GitHubReleaseAsset, checksum: GitHubReleaseAsset)? {
        let dmgs = assets.filter { $0.name == MacReleaseAssets.dmgName(for: version) }
        let checksums = assets.filter { $0.name == MacReleaseAssets.checksumName(for: version) }
        guard dmgs.count == 1, checksums.count == 1,
              isGitHubHTTPS(dmgs[0].browserDownloadURL),
              isGitHubHTTPS(checksums[0].browserDownloadURL) else { return nil }
        return (dmgs[0], checksums[0])
    }

    /// Downloads the checksum and DMG into a fresh private folder, proves the checksum names this
    /// DMG, and compares SHA-256 over the completed file. Returns the verified DMG, unopened.
    func downloadAndVerify(_ offer: UpdateOffer) async throws -> (dmg: URL, sha256: String) {
        try? FileManager.default.removeItem(at: workDirectory)
        try FileManager.default.createDirectory(at: workDirectory, withIntermediateDirectories: true)

        do {
            let checksumData: Data
            do {
                let (data, finalURL, status) = try await client.data(from: offer.checksum.browserDownloadURL)
                try Self.requireSecure(finalURL ?? offer.checksum.browserDownloadURL)
                guard status == 200, data.count <= Self.maximumChecksumBytes else { throw UpdateError.missingChecksum }
                checksumData = data
            } catch let error as UpdateError where error.isSecurityFailure {
                throw error
            } catch {
                throw UpdateError.missingChecksum
            }
            guard let checksumText = String(data: checksumData, encoding: .utf8) else {
                throw UpdateError.malformedChecksum
            }
            let expected = try Self.parseChecksum(checksumText, expectedFileName: offer.dmg.name)

            let (temporary, finalURL, status): (URL, URL?, Int)
            do {
                (temporary, finalURL, status) = try await client.download(from: offer.dmg.browserDownloadURL)
            } catch {
                throw UpdateError.network(error.localizedDescription)
            }
            let partial = workDirectory.appendingPathComponent(offer.dmg.name + ".partial")
            try FileManager.default.moveItem(at: temporary, to: partial)
            try Self.requireSecure(finalURL ?? offer.dmg.browserDownloadURL)
            guard status == 200 else { throw UpdateError.httpStatus(status) }

            let received = (try? FileManager.default.attributesOfItem(atPath: partial.path)[.size] as? Int) ?? -1
            if let size = offer.dmg.size, size != received {
                throw UpdateError.incompleteDownload(expected: size, received: max(0, received))
            }
            let actual = try Self.sha256(of: partial)
            guard actual == expected else { throw UpdateError.digestMismatch }

            let verified = workDirectory.appendingPathComponent(offer.dmg.name)
            try FileManager.default.moveItem(at: partial, to: verified)
            return (verified, actual)
        } catch {
            // Nothing unverified is left behind to be opened later by mistake.
            try? FileManager.default.removeItem(at: workDirectory)
            if error is UpdateError { throw error }
            throw UpdateError.network(error.localizedDescription)
        }
    }

    /// `shasum -a 256` output: exactly one line of 64 hex digits, whitespace, an optional `*`
    /// binary marker, then the file name, which must be the DMG being verified.
    static func parseChecksum(_ text: String, expectedFileName: String) throws -> String {
        let lines = text.split(whereSeparator: \.isNewline)
            .map { $0.trimmingCharacters(in: .whitespaces) }
            .filter { !$0.isEmpty }
        guard lines.count == 1 else { throw UpdateError.malformedChecksum }
        let line = lines[0]
        guard let space = line.firstIndex(where: { $0 == " " || $0 == "\t" }) else {
            throw UpdateError.malformedChecksum
        }
        let digest = String(line[..<space]).lowercased()
        guard digest.count == 64, digest.allSatisfy({ $0.isHexDigit && $0.isASCII }) else {
            throw UpdateError.malformedChecksum
        }
        var name = line[space...].trimmingCharacters(in: .whitespaces)
        if name.hasPrefix("*") { name.removeFirst() }
        guard !name.isEmpty else { throw UpdateError.malformedChecksum }
        guard name == expectedFileName else {
            throw UpdateError.checksumNamesDifferentFile(expected: expectedFileName, found: name)
        }
        return digest
    }

    static func sha256(of file: URL) throws -> String {
        let handle = try FileHandle(forReadingFrom: file)
        defer { try? handle.close() }
        var hasher = SHA256()
        while let chunk = try handle.read(upToCount: 1 << 20), !chunk.isEmpty {
            hasher.update(data: chunk)
        }
        return hasher.finalize().map { String(format: "%02x", $0) }.joined()
    }

    private func fetch(_ url: URL) async throws -> (Data, URL?, Int) {
        do {
            return try await client.data(from: url)
        } catch {
            throw UpdateError.network(error.localizedDescription)
        }
    }

    private static func requireSecure(_ url: URL) throws {
        guard url.scheme?.lowercased() == "https" else { throw UpdateError.insecureURL(url.absoluteString) }
    }

    private static func isGitHubHTTPS(_ url: URL) -> Bool {
        return url.scheme?.lowercased() == "https" && url.host?.lowercased() == "github.com"
    }
}

/// What the user sees at each decision. The AppKit presenter shows copyable alerts; tests record.
protocol UpdatePresenter: AnyObject, Sendable {
    func showUpToDate(installed: SemanticVersion, latest: SemanticVersion) async
    func showNewerWithoutMacDownload(installed: SemanticVersion, latest: SemanticVersion, releasePage: URL?) async
    func confirmDownload(_ offer: UpdateOffer) async -> Bool
    func confirmOpen(_ offer: UpdateOffer, verifiedDMG: URL, sha256: String) async -> Bool
    func open(verifiedDMG: URL) async
    func showFailure(_ error: UpdateError) async
}

/// The single running update check. A second request while one runs is ignored, and every path -
/// up to date, cancelled, opened, or failed - returns the flow to idle.
final class UpdateFlow: @unchecked Sendable {
    enum Phase: Equatable {
        case idle
        case checking
    }

    private let lock = NSLock()
    private var currentPhase = Phase.idle
    private let service: UpdateService
    private let presenter: UpdatePresenter
    private let installedVersion: String
    /// Called on each phase change so the menu can show "Checking for Updates…".
    var onPhaseChange: (@Sendable () -> Void)?

    init(service: UpdateService, presenter: UpdatePresenter, installedVersion: String) {
        self.service = service
        self.presenter = presenter
        self.installedVersion = installedVersion
    }

    var phase: Phase {
        lock.lock()
        defer { lock.unlock() }
        return currentPhase
    }

    /// Starts a check unless one is already running. Returns the task, or nil when suppressed.
    @discardableResult
    func start() -> Task<Void, Never>? {
        lock.lock()
        guard currentPhase == .idle else {
            lock.unlock()
            return nil
        }
        currentPhase = .checking
        lock.unlock()
        onPhaseChange?()

        return Task.detached { [self] in
            await self.run()
            self.returnToIdle()
        }
    }

    private func returnToIdle() {
        lock.lock()
        currentPhase = .idle
        lock.unlock()
        onPhaseChange?()
    }

    private func run() async {
        do {
            switch try await service.check(installedVersion: installedVersion) {
            case .upToDate(let installed, let latest):
                await presenter.showUpToDate(installed: installed, latest: latest)
            case .newerWithoutMacDownload(let installed, let latest, let page):
                await presenter.showNewerWithoutMacDownload(installed: installed, latest: latest, releasePage: page)
            case .available(let offer):
                guard await presenter.confirmDownload(offer) else { return }
                let verified = try await service.downloadAndVerify(offer)
                if await presenter.confirmOpen(offer, verifiedDMG: verified.dmg, sha256: verified.sha256) {
                    await presenter.open(verifiedDMG: verified.dmg)
                } else {
                    try? FileManager.default.removeItem(at: service.workDirectory)
                }
            }
        } catch let error as UpdateError {
            await presenter.showFailure(error)
        } catch {
            await presenter.showFailure(.network(error.localizedDescription))
        }
    }
}

/// The Settings row, as a pure function so its states can be tested without a menu.
enum UpdateMenuState {
    static func item(phase: UpdateFlow.Phase, captureLocked: Bool) -> (title: String, enabled: Bool) {
        let checking = phase != .idle
        return (checking ? "Checking for Updates…" : "Check for Updates…", !checking && !captureLocked)
    }
}
