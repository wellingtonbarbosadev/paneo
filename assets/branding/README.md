# Logo Paneo

A versão minimalista `paneo-logo-v2-minimal.png` é usada no README e nos
ícones do aplicativo. `Paneo.icns` contém os tamanhos para macOS e `paneo.ico`
contém sete tamanhos (16 a 256 px) para Windows e instalador.

Para regenerar os formatos nativos no macOS:

```sh
python3 assets/branding/build-icons.py
```

O script preserva a arte e a transparência, apenas redimensionando e
empacotando os arquivos com sips/iconutil. O build do macOS copia o ICNS
para o bundle; o projeto Windows incorpora o ICO no executável.

## Proposta anterior

Proposta v1 gerada com a ferramenta nativa image_gen em 2026-10-22.
Arquivo: `paneo-logo-v1.png`, com transparência. Símbolo inspirado em janelas
conectadas formando um P, com acabamento de vidro azul/ciano.
Ainda não integrado aos ícones dos executáveis.

## Prompt utilizado

Use case: logo-brand. Create a polished original logo for Paneo, a desktop application that brings individual Windows application windows into native macOS windows. Deliver ONE standalone centered symbol, no text, on a truly transparent background. Design: two interlocking rounded window panes subtly forming an uppercase P through negative space, a clear memorable silhouette with generous internal openings that remains readable at small app-icon sizes. Contemporary restrained precision, consistent geometry, subtle translucent glass inspired by the product Liquid Glass interface, luminous blue and cyan with a restrained indigo edge, crisp edges and very subtle depth. Symbol occupies 75 percent of square canvas. No enclosing app tile, no mockup, no presentation board, no lettering, no watermark, no arrows, no cables, no monitor stand, no Apple or Microsoft logos. The result should feel like a finished premium desktop software brand mark rather than an illustration.
