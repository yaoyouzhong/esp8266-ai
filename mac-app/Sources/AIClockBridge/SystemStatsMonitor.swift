import Foundation

// System-wide CPU% and memory-used% for the net-speed page (device + mirror).
// CPU comes from HOST_CPU_LOAD_INFO tick deltas between calls; memory is
// (active + wired + compressed) / physical, roughly Activity Monitor's "used".
// Recomputes at most once per second no matter how often it's asked.
final class SystemStatsMonitor {
    static let shared = SystemStatsMonitor()

    private let lock = NSLock()
    private var lastTicks: (busy: UInt64, idle: UInt64)?
    private var cached: (cpu: Int, mem: Int) = (0, 0)
    private var cachedAt = Date.distantPast

    func snapshot() -> (cpu: Int, mem: Int) {
        lock.lock()
        defer { lock.unlock() }
        if Date().timeIntervalSince(cachedAt) < 1.0 { return cached }
        cachedAt = Date()
        if let ticks = Self.cpuTicks() {
            let busy = ticks.user + ticks.system + ticks.nice
            if let last = lastTicks {
                let dBusy = busy &- last.busy
                let dIdle = ticks.idle &- last.idle
                let total = dBusy + dIdle
                if total > 0 { cached.cpu = Int((Double(dBusy) / Double(total) * 100).rounded()) }
            }
            lastTicks = (busy, ticks.idle)
        }
        cached.mem = Self.memPct()
        return cached
    }

    private static func cpuTicks() -> (user: UInt64, system: UInt64, idle: UInt64, nice: UInt64)? {
        var size = mach_msg_type_number_t(MemoryLayout<host_cpu_load_info_data_t>.size
                                          / MemoryLayout<integer_t>.size)
        var info = host_cpu_load_info_data_t()
        let kr = withUnsafeMutablePointer(to: &info) {
            $0.withMemoryRebound(to: integer_t.self, capacity: Int(size)) {
                host_statistics(mach_host_self(), HOST_CPU_LOAD_INFO, $0, &size)
            }
        }
        guard kr == KERN_SUCCESS else { return nil }
        let t = info.cpu_ticks // (USER, SYSTEM, IDLE, NICE)
        return (UInt64(t.0), UInt64(t.1), UInt64(t.2), UInt64(t.3))
    }

    private static func memPct() -> Int {
        var size = mach_msg_type_number_t(MemoryLayout<vm_statistics64_data_t>.size
                                          / MemoryLayout<integer_t>.size)
        var vm = vm_statistics64_data_t()
        let kr = withUnsafeMutablePointer(to: &vm) {
            $0.withMemoryRebound(to: integer_t.self, capacity: Int(size)) {
                host_statistics64(mach_host_self(), HOST_VM_INFO64, $0, &size)
            }
        }
        guard kr == KERN_SUCCESS else { return 0 }
        let used = (UInt64(vm.active_count) + UInt64(vm.wire_count)
                    + UInt64(vm.compressor_page_count)) * UInt64(vm_kernel_page_size)
        let total = ProcessInfo.processInfo.physicalMemory
        guard total > 0 else { return 0 }
        return Int((Double(used) / Double(total) * 100).rounded())
    }
}
