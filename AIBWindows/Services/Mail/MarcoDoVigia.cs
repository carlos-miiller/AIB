using System;
using System.Globalization;
using System.IO;

namespace AIB.Services.Mail;

/// <summary>
/// Quando o último digest rodou. Um arquivo de uma linha em <c>~/.AIB/email/</c>.
/// <para>
/// Existe por um defeito visto em produção: o marcador vivia em memória, então TODO arranque
/// do programa depois das 8h25 disparava um digest — abrir e fechar a AIB três vezes numa
/// manhã custava três leituras da caixa e três chamadas ao modelo, dizendo a mesma coisa.
/// </para>
/// <para>
/// Guarda um instante e nada mais. É a menor coisa que precisava sobreviver ao fechamento do
/// programa, e não há motivo para ela dividir arquivo com o estado das caixas, que é por
/// endereço.
/// </para>
/// </summary>
public sealed class MarcoDoVigia
{
    private readonly string _caminho;

    public MarcoDoVigia(string? raizDeDados = null)
    {
        string baseDir = string.IsNullOrWhiteSpace(raizDeDados)
            ? DirectoryService.DataDir
            : raizDeDados!;

        _caminho = Path.Combine(baseDir, "email", "ultimo-digest.txt");
    }

    public string Caminho => _caminho;

    /// <summary>Hora LOCAL do último digest, ou nulo se nunca rodou.</summary>
    public DateTime? UltimoDigest
    {
        get
        {
            try
            {
                if (!File.Exists(_caminho)) return null;

                string texto = File.ReadAllText(_caminho).Trim();

                return DateTime.TryParse(texto, CultureInfo.InvariantCulture,
                                         DateTimeStyles.RoundtripKind, out var quando)
                    ? quando.ToLocalTime()
                    : null;
            }
            catch
            {
                // Sem o marcador, o pior que acontece é um digest a mais. Derrubar o vigia por
                // causa de um arquivo de uma linha seria trocar o barato pelo caro.
                return null;
            }
        }
    }

    public void GravarDigest(DateTime quandoLocal)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_caminho)!);
            File.WriteAllText(_caminho, quandoLocal.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[VIGIA] não consegui gravar o marco do digest: {ex.Message}");
        }
    }
}
