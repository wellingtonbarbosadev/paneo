import Foundation

enum RemoteConnectionFailure {
    static func message(pinMismatch: Bool) -> String {
        pinMismatch
            ? "Certificado do host diferente do esperado. Revogue o pareamento no Windows e pareia de novo."
            : "Falha na conexão TLS. Confira endereço e porta."
    }

    static func isTransient(_ message: String) -> Bool {
        guard !message.hasPrefix("Autenticação recusada"),
              !message.hasPrefix("Certificado do host diferente"),
              !message.hasPrefix("Mensagem inválida ou incompatível") else { return false }
        return [
            "Falha na conexão TLS.", "Conexão encerrada.", "Tempo esgotado ao conectar ou autenticar.",
            "O host encerrou a conexão.", "Falha no envio; comandos descartados.",
            "Falha ao ler a resposta do host.", "Host sem resposta."
        ].contains { message.hasPrefix($0) }
    }
}
