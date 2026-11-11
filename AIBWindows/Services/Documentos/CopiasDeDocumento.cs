using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace AIB.Services.Documentos;

/// <summary>
/// Guarda as últimas cópias de documentos antes de gravar por cima, organizadas por arquivo em
/// <c>~/.AIB/doc-backups/</c>. Permite listar cópias, restaurar ("desfazer") e limpar versões antigas.
/// </summary>
public sealed class CopiasDeDocumento
{
    public const int PorArquivo = 5;
    public static readonly TimeSpan Validade = TimeSpan.FromDays(90);

    private readonly Func<DateTime> _agora;

    private static readonly Regex PadraoNome = new(
        @"^(?<data>\d{4}-\d{2}-\d{2} \d{6})(?: \((?<seq>\d+)\))?$",
        RegexOptions.Compiled);

    /// <param name="raizDeDados">A pasta ~/.AIB (null → Path.Combine(UserProfile, ".AIB")). Pasta = raiz\doc-backups.</param>
    /// <param name="agora">Relógio injetado (null → DateTime.Now).</param>
    public CopiasDeDocumento(string? raizDeDados = null, Func<DateTime>? agora = null)
    {
        string raiz = raizDeDados ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".AIB");
        Pasta = Path.Combine(raiz, "doc-backups");
        _agora = agora ?? (() => DateTime.Now);
    }

    public string Pasta { get; }

    /// <summary>
    /// Determinística (vai no texto do plano, comparado entre cartão e gravação):
    /// <c>~/.AIB/doc-backups/&lt;pasta do arquivo&gt;</c>.
    /// </summary>
    public static string Descricao(string caminho) =>
        $"~/.AIB/doc-backups/{NomeDaPasta(caminho)}";

    /// <summary>
    /// Copia o arquivo atual, devolve o caminho completo da cópia e poda para as 5 mais novas.
    /// </summary>
    public string Guardar(string caminho)
    {
        if (!File.Exists(caminho))
            throw new FileNotFoundException($"O arquivo '{caminho}' não existe.", caminho);

        string pasta = PastaDe(caminho);
        Directory.CreateDirectory(pasta);

        string origem = Path.Combine(pasta, "origem.txt");
        try
        {
            File.WriteAllText(origem, Path.GetFullPath(caminho), Encoding.UTF8);
        }
        catch { }

        DateTime agora = _agora();
        string dataBase = agora.ToString("yyyy-MM-dd HHmmss", CultureInfo.InvariantCulture);
        string ext = Path.GetExtension(caminho);
        string nomeCopia = $"{dataBase}{ext}";
        string destino = Path.Combine(pasta, nomeCopia);

        int seq = 2;
        while (File.Exists(destino))
        {
            nomeCopia = $"{dataBase} ({seq}){ext}";
            destino = Path.Combine(pasta, nomeCopia);
            seq++;
        }

        File.Copy(caminho, destino, overwrite: true);

        var todas = Copias(caminho);
        if (todas.Count > PorArquivo)
        {
            foreach (var velha in todas.Skip(PorArquivo))
            {
                try { File.Delete(velha); } catch { }
            }
        }

        return destino;
    }

    /// <summary>Cópias daquele arquivo, mais nova primeiro.</summary>
    public IReadOnlyList<string> Copias(string caminho)
    {
        string pasta = PastaDe(caminho);
        if (!Directory.Exists(pasta)) return Array.Empty<string>();

        return Directory.EnumerateFiles(pasta)
            .Where(f => !string.Equals(Path.GetFileName(f), "origem.txt", StringComparison.OrdinalIgnoreCase))
            .Select(f => (Caminho: f, Info: ObterDataESeq(f)))
            .OrderByDescending(x => x.Info.Data)
            .ThenByDescending(x => x.Info.Seq)
            .Select(x => x.Caminho)
            .ToList();
    }

    /// <summary>
    /// Apaga cópias com mais de Validade (pela data no nome; se ilegível, LastWriteTimeUtc) e pastas
    /// que ficarem vazias; devolve quantas apagou; nunca lança.
    /// </summary>
    public int Limpar()
    {
        if (!Directory.Exists(Pasta)) return 0;

        DateTime agora = _agora();
        int apagadas = 0;

        string[] subpastas;
        try
        {
            subpastas = Directory.GetDirectories(Pasta);
        }
        catch
        {
            return 0;
        }

        foreach (var sub in subpastas)
        {
            try
            {
                var arquivos = Directory.GetFiles(sub);
                var copias = arquivos
                    .Where(f => !string.Equals(Path.GetFileName(f), "origem.txt", StringComparison.OrdinalIgnoreCase))
                    .ToList();

                foreach (var copia in copias)
                {
                    try
                    {
                        string semExt = Path.GetFileNameWithoutExtension(copia);
                        var m = PadraoNome.Match(semExt);
                        TimeSpan idade;
                        if (m.Success && DateTime.TryParseExact(
                            m.Groups["data"].Value, "yyyy-MM-dd HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dtNome))
                        {
                            idade = agora - dtNome;
                        }
                        else
                        {
                            DateTime utcAgora = agora.Kind == DateTimeKind.Utc ? agora : agora.ToUniversalTime();
                            idade = utcAgora - File.GetLastWriteTimeUtc(copia);
                        }

                        if (idade > Validade)
                        {
                            File.Delete(copia);
                            apagadas++;
                        }
                    }
                    catch
                    {
                        // falha de IO em uma cópia não impede as outras
                    }
                }

                var restantes = Directory.GetFiles(sub)
                    .Where(f => !string.Equals(Path.GetFileName(f), "origem.txt", StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (restantes.Count == 0)
                {
                    string origem = Path.Combine(sub, "origem.txt");
                    if (File.Exists(origem))
                    {
                        try { File.Delete(origem); } catch { }
                    }
                    try { Directory.Delete(sub, true); } catch { }
                }
            }
            catch
            {
                // falha em uma pasta não impede as outras
            }
        }

        return apagadas;
    }

    /// <summary>
    /// Desfazer: volta o arquivo ao estado anterior da gravação. Recusa se o arquivo mudou depois
    /// da gravação (impressão diferente). Guarda o estado atual antes de restaurar.
    /// </summary>
    public string Restaurar(string caminho, string copia, string? impressaoDepois)
    {
        string? atual = Gravacao.Impressao(caminho);
        if (!string.Equals(atual, impressaoDepois, StringComparison.Ordinal))
        {
            throw new ErroDeDocumento(
                $"'{Path.GetFileName(caminho)}' mudou depois da gravação; restaurar apagaria essas mudanças. "
                + $"Nada foi feito. A cópia continua em {copia}.");
        }

        if (!File.Exists(copia))
            throw new ErroDeDocumento($"a cópia '{copia}' não existe mais.");

        Guardar(caminho);

        string pasta = Path.GetDirectoryName(Path.GetFullPath(caminho)) ?? ".";
        string temporario = Path.Combine(pasta, $"~aib-{Guid.NewGuid():N}{Path.GetExtension(caminho)}");
        try
        {
            File.Copy(copia, temporario, overwrite: true);
            File.Move(temporario, caminho, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporario)) File.Delete(temporario); } catch { }
        }

        return $"'{Path.GetFileName(caminho)}' voltou ao que era antes da gravação.";
    }

    private string PastaDe(string caminho) =>
        Path.Combine(Pasta, NomeDaPasta(caminho));

    internal static string NomeDaPasta(string caminho)
    {
        string nome = Path.GetFileNameWithoutExtension(caminho);
        try
        {
            if (File.Exists(caminho))
            {
                var dir = new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(caminho)) ?? ".");
                var fi = dir.EnumerateFiles(Path.GetFileName(caminho)).FirstOrDefault();
                if (fi != null) nome = Path.GetFileNameWithoutExtension(fi.Name);
            }
        }
        catch { }

        string limpo = SanitizarNome(nome);
        if (limpo.Length == 0) limpo = "documento";

        string hash = HashCaminho(caminho);
        return $"{limpo} ({hash})";
    }

    private static string HashCaminho(string caminho)
    {
        string normalizado = Path.GetFullPath(caminho).ToLowerInvariant();
        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalizado));
        return Convert.ToHexString(bytes)[..8].ToLowerInvariant();
    }

    private static string SanitizarNome(string nome)
    {
        char[] invalidos = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(nome.Length);
        foreach (char c in nome)
            sb.Append(invalidos.Contains(c) ? '_' : c);
        return sb.ToString();
    }

    private static (DateTime Data, int Seq) ObterDataESeq(string caminhoArquivo)
    {
        string semExt = Path.GetFileNameWithoutExtension(caminhoArquivo);
        var m = PadraoNome.Match(semExt);
        if (m.Success && DateTime.TryParseExact(
            m.Groups["data"].Value, "yyyy-MM-dd HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
        {
            int seq = m.Groups["seq"].Success && int.TryParse(m.Groups["seq"].Value, out int s) ? s : 1;
            return (dt, seq);
        }

        try
        {
            var fi = new FileInfo(caminhoArquivo);
            return (fi.LastWriteTime, 1);
        }
        catch
        {
            return (DateTime.MinValue, 1);
        }
    }
}
