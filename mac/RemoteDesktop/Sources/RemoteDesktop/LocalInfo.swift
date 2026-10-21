import AppKit
import Darwin

/// Informações reais do próprio Mac, lidas localmente para exibição na janela.
///
/// Fontes: `ProcessInfo` (Foundation) e `sysctlbyname` (Darwin). Nenhuma leitura
/// ou envio pela rede; nenhum dado do Windows, que ainda não é acessado.
@MainActor
enum LocalInfo {

    /// Texto exibido no painel "Informações locais" no momento em que a janela abre.
    static var descriptionText: String {
        let info = ProcessInfo.processInfo
        var lines: [String] = []

        let version = info.operatingSystemVersion
        lines.append(
            "Sistema: macOS \(version.majorVersion).\(version.minorVersion).\(version.patchVersion) — \(info.operatingSystemVersionString)"
        )
        lines.append("Modelo do equipamento: \(systemValue("hw.model") ?? "indisponível")")
        lines.append("Arquitetura da CPU (runtime): \(systemValue("hw.machine") ?? "indisponível")")
        lines.append("Arquitetura do executável (build): \(buildArchitecture)")
        if let cpuBrand = systemValue("machdep.cpu.brand_string"), !cpuBrand.isEmpty {
            lines.append("Processador: \(cpuBrand)")
        }
        lines.append(
            "Processadores: \(info.processorCount) totais / \(info.activeProcessorCount) ativos"
        )
        let memoryFormatter = ByteCountFormatter()
        memoryFormatter.countStyle = .memory
        lines.append("Memória física: \(memoryFormatter.string(fromByteCount: Int64(clamping: info.physicalMemory)))")
        lines.append("Tempo ligado: \(SystemUptime.text)")
        lines.append("Usuário: \(info.userName) — Host: \(info.hostName)")
        lines.append("Processo: \(info.processName) (PID \(info.processIdentifier))")

        lines.append("")
        lines.append("Leitura única, local, no momento em que a janela abriu. Não há consulta à rede nem ao agente Windows nesta versão.")

        return lines.joined(separator: "\n")
    }

    /// Arquitetura embutida no binário, decidida em tempo de compilação.
    static var buildArchitecture: String {
        #if arch(arm64)
        return "arm64"
        #elseif arch(x86_64)
        return "x86_64"
        #else
        return "desconhecida para este alvo"
        #endif
    }

    /// Consulta sysctl local; não faz entrada/saída de rede.
    static func systemValue(_ key: String) -> String? {
        var size = 0
        guard sysctlbyname(key, nil, &size, nil, 0) == 0, size > 0 else { return nil }
        var buffer = [CChar](repeating: 0, count: size)
        guard sysctlbyname(key, &buffer, &size, nil, 0) == 0 else { return nil }
        var bytes = buffer.map { UInt8(bitPattern: $0) }
        while bytes.last == 0 {
            bytes.removeLast()
        }
        return String(decoding: bytes, as: UTF8.self)
    }
}

/// Tempo de atividade do sistema, formatado com API do Foundation.
@MainActor
enum SystemUptime {

    static var text: String {
        let formatter = DateComponentsFormatter()
        formatter.allowedUnits = [.day, .hour, .minute]
        formatter.unitsStyle = .abbreviated
        return formatter.string(from: ProcessInfo.processInfo.systemUptime) ?? "indisponível"
    }
}
