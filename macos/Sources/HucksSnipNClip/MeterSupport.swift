import Darwin.Mach
import Foundation

/// Matches the approved Windows meter response: quick attack, slower release, and a brief hold so
/// a short peak remains readable in an 18-point menu-bar icon.
final class MeterSmoother {
    private static let attackMilliseconds = 150.0
    private static let releaseMilliseconds = 400.0
    private static let peakHoldMilliseconds = 800

    private(set) var level = 0.0
    private var holdRemaining = 0

    @discardableResult
    func update(target rawTarget: Double, elapsedMilliseconds: Int) -> Double {
        let target = min(1.0, max(0.0, rawTarget))
        let elapsed = max(0, elapsedMilliseconds)

        if target >= level {
            let amount = min(1.0, Double(elapsed) / Self.attackMilliseconds)
            level += (target - level) * amount
            holdRemaining = Self.peakHoldMilliseconds
        } else if holdRemaining > 0 {
            holdRemaining = max(0, holdRemaining - elapsed)
        } else {
            let amount = min(1.0, Double(elapsed) / Self.releaseMilliseconds)
            level += (target - level) * amount
        }

        return level
    }

    func reset() {
        level = 0.0
        holdRemaining = 0
    }
}

/// Reads total host CPU time deltas, rather than this app's process CPU, so the right post warns
/// about game/creative workload pressure just as the Windows implementation does.
final class SystemLoadSampler {
    private var previousTicks: (user: UInt64, system: UInt64, idle: UInt64, nice: UInt64)?

    func sample() -> Double {
        var load = host_cpu_load_info_data_t()
        var count = mach_msg_type_number_t(
            MemoryLayout<host_cpu_load_info_data_t>.size / MemoryLayout<integer_t>.size)
        let result = withUnsafeMutablePointer(to: &load) { pointer in
            pointer.withMemoryRebound(to: integer_t.self, capacity: Int(count)) {
                host_statistics(mach_host_self(), HOST_CPU_LOAD_INFO, $0, &count)
            }
        }
        guard result == KERN_SUCCESS else { return 0.0 }

        let ticks = (
            user: UInt64(load.cpu_ticks.0),
            system: UInt64(load.cpu_ticks.1),
            idle: UInt64(load.cpu_ticks.2),
            nice: UInt64(load.cpu_ticks.3))
        guard let previousTicks else {
            self.previousTicks = ticks
            return 0.0
        }
        self.previousTicks = ticks

        let user = ticks.user &- previousTicks.user
        let system = ticks.system &- previousTicks.system
        let idle = ticks.idle &- previousTicks.idle
        let nice = ticks.nice &- previousTicks.nice
        let total = user + system + idle + nice
        guard total > 0 else { return 0.0 }
        return min(1.0, max(0.0, Double(user + system + nice) / Double(total)))
    }

    func reset() {
        previousTicks = nil
    }
}
