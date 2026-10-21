// swift-tools-version:6.0
import PackageDescription

let package = Package(
    name: "RemoteDesktop",
    platforms: [
        // Mínimo provisório; será revisado após inventário dos equipamentos reais (PLAN.md, etapa 0).
        .macOS(.v14)
    ],
    targets: [
        .executableTarget(
            name: "RemoteDesktop",
            path: "Sources/RemoteDesktop"
        )
    ]
)
