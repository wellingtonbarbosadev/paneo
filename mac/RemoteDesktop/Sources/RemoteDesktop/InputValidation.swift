import Darwin
import Foundation

/// Validação de formato das entradas de destino, executada apenas neste Mac.
///
/// Nenhuma função aqui abre socket, resolve DNS (guarde: nome de host não é
/// convertido em IP) ou contata o Windows. Transporte ainda não implementado.
enum InputValidation {

    /// Retorna todos os problemas encontrados; lista vazia significa formato válido.
    static func problems(address: String, port: String) -> [String] {
        addressProblems(address) + portProblems(port)
    }

    // MARK: Endereço

    static func addressProblems(_ raw: String) -> [String] {
        let address = raw.trimmingCharacters(in: .whitespacesAndNewlines)

        guard !address.isEmpty else {
            return ["o endereço está em branco"]
        }
        guard !address.contains(where: \Character.isWhitespace) else {
            return ["o endereço não pode conter espaços"]
        }
        guard address.count <= 253 else {
            return ["o endereço é longo demais (máximo de 253 caracteres)"]
        }

        if looksLikeIPv4(address) {
            return ipv4Problems(address)
        }
        if address.contains(":") {
            return isIPv6Literal(address)
                ? []
                : ["não parece um endereço IPv6 válido"]
        }
        return hostnameProblems(address)
    }

    /// Somente dígitos e pontos (com ao menos um ponto) são tratados como IPv4.
    private static func looksLikeIPv4(_ value: String) -> Bool {
        value.contains(".") && value.allSatisfy { $0.isASCII && ($0.isNumber || $0 == ".") }
    }

    private static func ipv4Problems(_ value: String) -> [String] {
        let octets = value.split(separator: ".", omittingEmptySubsequences: false)
        guard octets.count == 4 else {
            return ["IPv4 precisa de 4 blocos separados por pontos (ex.: 192.168.1.20)"]
        }
        for octet in octets {
            guard !octet.isEmpty,
                  octet.count <= 3,
                  octet.allSatisfy({ $0.isASCII && $0.isNumber }),
                  let number = Int(octet),
                  (0...255).contains(number)
            else {
                return ["bloco de IPv4 inválido: “\(octet)” deve ser um número de 0 a 255"]
            }
        }
        return []
    }

    /// Checagem literal (sem DNS): usa inet_pton, que não faz consulta de rede.
    private static func isIPv6Literal(_ literal: String) -> Bool {
        var value = literal
        if value.hasPrefix("["), value.hasSuffix("]"), value.count > 2 {
            value.removeFirst()
            value.removeLast()
        }
        var storage = in6_addr()
        return value.withCString { pointer in
            inet_pton(AF_INET6, pointer, &storage) == 1
        }
    }

    /// Nome de host ou nome de máquina (rótulo único aceito, ex.: "localhost" ou "PC-WELLINGTON").
    private static func hostnameProblems(_ value: String) -> [String] {
        let allowed = CharacterSet(charactersIn:
            "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-_.")
        guard value.unicodeScalars.allSatisfy({ allowed.contains($0) }) else {
            return ["o endereço contém caracteres não permitidos para nome de host"]
        }

        var labels = value.split(separator: ".", omittingEmptySubsequences: false)
        if labels.count > 1, labels.last?.isEmpty == true {
            // Ponto final em FQDN é tolerado, desde que o resto seja válido.
            labels.removeLast()
        }
        for label in labels {
            guard !label.isEmpty else {
                return ["rótulo vazio no nome de host (pontos consecutivos?)"]
            }
            guard label.count <= 63 else {
                return ["rótulo “\(label.prefix(24))…” longo demais (máximo de 63 caracteres)"]
            }
        }
        return []
    }

    // MARK: Porta

    static func portProblems(_ raw: String) -> [String] {
        let value = raw.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !value.isEmpty else {
            return ["a porta está em branco"]
        }
        guard value.allSatisfy({ $0.isASCII && $0.isNumber }) else {
            return ["a porta aceita apenas dígitos"]
        }
        guard let number = Int(value), (1...65535).contains(number) else {
            return ["a porta deve estar entre 1 e 65535"]
        }
        return []
    }
}
