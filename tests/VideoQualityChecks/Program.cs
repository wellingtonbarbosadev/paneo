using WindowProbe.Remote;

byte[] source = new byte[1920 * 1080 * 4];
source[0] = 12;
byte[]? buffer = null;
var scaled = VideoScaling.Apply(source, 1920, 1080, 1920 * 4, 1280, ref buffer);
if ((scaled.Width, scaled.Height, scaled.Stride) != (1280, 720, 5120) || scaled.Pixels[0] != 12)
    throw new Exception("Escala 1080p não preservou proporção/conteúdo.");
byte[] reused = scaled.Pixels;
var next = VideoScaling.Apply(source, 1920, 1080, 1920 * 4, 1280, ref buffer);
if (!ReferenceEquals(reused, next.Pixels)) throw new Exception("Buffer de escala não foi reutilizado.");
var native = VideoScaling.Apply(source, 1920, 1080, 1920 * 4, 0, ref buffer);
if (!ReferenceEquals(source, native.Pixels)) throw new Exception("Modo nativo alterou o quadro.");
var small = VideoScaling.Apply(source, 1280, 720, 1280 * 4, 1920, ref buffer);
if (!ReferenceEquals(source, small.Pixels)) throw new Exception("A escala aumentou o quadro.");
if (VideoScaling.EncodedDimension(1920) != 1920 || VideoScaling.EncodedDimension(1080) != 1088)
    throw new Exception("Dimensões coded não refletem padding de 16.");
var downscaledViewport = VideoScaling.ScaleViewport((0, 0, 1920, 1080), 1920, 1080, 1280, 720);
if (downscaledViewport != (0, 0, 1280, 720))
    throw new Exception("Viewport 1080p não acompanhou a resolução ativa escalada.");
var popupViewport = VideoScaling.ScaleViewport((200, 120, 1000, 600), 2000, 1200, 1000, 600);
if (popupViewport != (100, 60, 500, 300))
    throw new Exception("Viewport do popup não acompanhou o quadro composto escalado.");
if (!VideoScaling.TryMapInput(.5, .5, 1, 1088d / 1080, out _, out double centerY)
    || Math.Abs(centerY - 544d / 1080) > .001)
    throw new Exception("Centro do quadro não foi mapeado para a captura ativa.");
foreach (string surface in new[] { "monitor", "popup" })
{
    if (!VideoScaling.TryMapInput(.999, .992, 1, 1088d / 1080, out _, out double lowerY) || lowerY >= 1)
        throw new Exception($"Ponto inferior direito válido foi recusado em {surface}.");
    if (VideoScaling.TryMapInput(.5, .997, 1, 1088d / 1080, out _, out _))
        throw new Exception($"Padding codificado aceitou entrada remota em {surface}.");
}
if (!VideoScaling.TryMapInput(.5, .5, 1, 1, out _, out double nativeY) || nativeY != .5)
    throw new Exception("Mudança de resolução sem mudança de viewport alterou a coordenada.");
try
{
    VideoScaling.Apply(source, 1920, 1080, 1920 * 4 - 1, 1280, ref buffer);
    throw new Exception("Stride inválido foi aceito.");
}
catch (ArgumentException) { }
Console.WriteLine("VideoScaling PASS");
