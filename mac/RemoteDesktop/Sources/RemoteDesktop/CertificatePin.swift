import CryptoKit
import Foundation
import Security

/// Verificação exata do certificado apresentado pelo host.
///
/// O usuário informa o SHA-256 do DER do certificado folha. Não existe caminho
/// de confiança por CA do sistema, "continuar mesmo assim" ou trust-all: se o
/// hash não bater, o handshake TLS é rejeitado.
enum CertificatePin {

    /// Aceita 64 dígitos hexadecimais, com ou sem ':', espaços ou '-'.
    static func normalizedFingerprint(_ raw: String) -> String? {
        var text = raw.trimmingCharacters(in: .whitespacesAndNewlines).lowercased()
        if text.hasPrefix("sha256:") {
            text.removeFirst("sha256:".count)
        }
        let hex = text.filter { character in
            character != ":" && character != "-" && !character.isWhitespace
        }
        guard hex.count == 64,
              hex.unicodeScalars.allSatisfy({ $0.isASCII && CharacterSet(charactersIn: "0123456789abcdef").contains($0) })
        else {
            return nil
        }
        return hex
    }

    static func sha256Hex(ofDER der: Data) -> String {
        SHA256.hash(data: der)
            .map { String(format: "%02x", $0) }
            .joined()
    }

    /// Marca o handshake como aceitável somente quando o DER do certificado
    /// folha tem exatamente o SHA-256 informado.
    static func matches(trust: SecTrust, expectedSHA256Hex: String) -> Bool {
        guard let chain = SecTrustCopyCertificateChain(trust) as? [SecCertificate],
              let leaf = chain.first
        else {
            return false
        }
        let der = SecCertificateCopyData(leaf) as Data
        let actual = sha256Hex(ofDER: der)
        return constantTimeEqual(actual, expectedSHA256Hex)
    }

    private static func constantTimeEqual(_ lhs: String, _ rhs: String) -> Bool {
        let left = Array(lhs.utf8)
        let right = Array(rhs.utf8)
        guard left.count == right.count else { return false }
        var difference: UInt8 = 0
        for index in left.indices {
            difference |= left[index] ^ right[index]
        }
        return difference == 0
    }
}
