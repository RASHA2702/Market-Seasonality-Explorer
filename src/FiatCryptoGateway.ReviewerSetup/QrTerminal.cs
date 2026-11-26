using System.Text;
using Net.Codecrete.QrCodeGenerator;

namespace FiatCryptoGateway.ReviewerSetup;

/// <summary>Draws a QR code with block characters, two modules per character cell, dark on light whatever the terminal's colours.</summary>
public static class QrTerminal
{
    /// <summary>The quiet zone the QR specification asks for, in modules.</summary>
    public const int Border = 4;

    private const string DarkOnLight = "\u001b[30;47m";
    private const string Reset = "\u001b[0m";

    public static IReadOnlyList<string> Render(string text, bool colours = true)
    {
        var code = QrCode.EncodeText(text, QrCode.Ecc.Medium);
        bool Dark(int x, int y) => x >= 0 && y >= 0 && x < code.Size && y < code.Size && code.GetModule(x, y);

        var lines = new List<string>();
        for (var y = -Border; y < code.Size + Border; y += 2)
        {
            var line = new StringBuilder();
            for (var x = -Border; x < code.Size + Border; x++)
            {
                line.Append((Dark(x, y), Dark(x, y + 1)) switch
                {
                    (true, true) => '█',
                    (true, false) => '▀',
                    (false, true) => '▄',
                    _ => ' ',
                });
            }

            lines.Add(colours ? DarkOnLight + line + Reset : line.ToString());
        }

        return lines;
    }
}
