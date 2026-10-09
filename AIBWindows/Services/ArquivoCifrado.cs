using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace AIB.Services;

/// <summary>
/// Texto gravado cifrado pelo DPAPI do usuário do Windows, como o <c>profile.dat</c> das
/// configurações: só a conta que gravou lê, e a cópia do arquivo para outra máquina ou outro
/// usuário não abre.
/// <para>
/// Não protege de um programa rodando na mesma conta — esse pede a mesma chave ao Windows. O
/// que sai do alcance é o disco copiado, o backup e o outro usuário da máquina. O preço é o
/// mesmo: reinstalado o Windows, nada disto abre. Por isso existe a exportação em texto claro
/// (<see cref="Memory.CifraDaMemoria.Exportar"/>).
/// </para>
/// <para>
/// Dois formatos. O BINÁRIO (<see cref="Gravar"/>) é o do <c>profile.dat</c>, para arquivo
/// novo com extensão <c>.dat</c>. O de LINHA (<see cref="Cifrar"/>) é texto — a marca
/// <see cref="Marca"/> e o resto em base64 — para o arquivo que já existia com nome e formato
/// conhecidos: o <c>raw.jsonl</c> continua sendo um arquivo de linhas em que só se acrescenta,
/// e linha antiga em texto claro convive com linha nova cifrada até a migração passar.
/// </para>
/// </summary>
public static class ArquivoCifrado
{
    /// <summary>O começo de toda linha cifrada. Nenhum JSON nem markdown do AIB começa assim.</summary>
    public const string Marca = "aib1:";

    private static readonly UTF8Encoding SemBom = new(encoderShouldEmitUTF8Identifier: false);

    // ── Linha ─────────────────────────────────────────────────────────────

    /// <summary>O texto (uma linha, ou um arquivo inteiro) como UMA linha cifrada.</summary>
    public static string Cifrar(string claro) =>
        Marca + Convert.ToBase64String(
            ProtectedData.Protect(SemBom.GetBytes(claro ?? ""), null, DataProtectionScope.CurrentUser));

    /// <summary>Se a linha é das cifradas.</summary>
    public static bool Cifrada(string? linha) =>
        linha != null && linha.StartsWith(Marca, StringComparison.Ordinal);

    /// <summary>
    /// A linha em texto claro. A que já está em claro volta como veio. Falso quando é cifrada e
    /// não abre — gravada por outra conta, ou cortada por uma queda no meio da escrita.
    /// </summary>
    public static bool Abrir(string linha, out string claro)
    {
        claro = linha;
        if (!Cifrada(linha)) return true;

        try
        {
            byte[] cifrado = Convert.FromBase64String(linha.Substring(Marca.Length).Trim());
            claro = SemBom.GetString(ProtectedData.Unprotect(cifrado, null, DataProtectionScope.CurrentUser));
            return true;
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            claro = "";
            return false;
        }
    }

    /// <summary>A linha em texto claro, ou ela mesma quando não abre. Para quem só exibe.</summary>
    public static string Abrir(string linha) => Abrir(linha, out string claro) ? claro : linha;

    /// <summary>
    /// Acrescenta ao fim do arquivo, cifrando cada linha de <paramref name="texto"/> em
    /// separado: é o que mantém o arquivo um arquivo de linhas.
    /// </summary>
    public static void Acrescentar(string caminho, string texto)
    {
        var linhas = (texto ?? "").Split('\n')
            .Select(l => l.TrimEnd('\r'))
            .Where(l => l.Length > 0)
            .Select(Cifrar);

        string bloco = string.Concat(linhas.Select(l => l + Environment.NewLine));
        if (bloco.Length == 0) return;

        Directory.CreateDirectory(Path.GetDirectoryName(caminho)!);

        // Arquivo que não termina em quebra de linha (editado à mão, ou cortado por uma queda):
        // sem isto a linha nova colaria na última, e as duas se perderiam.
        if (!TerminaEmQuebra(caminho)) bloco = Environment.NewLine + bloco;

        File.AppendAllText(caminho, bloco, SemBom);
    }

    private static bool TerminaEmQuebra(string caminho)
    {
        if (!File.Exists(caminho)) return true;

        using var arquivo = new FileStream(caminho, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (arquivo.Length == 0) return true;

        arquivo.Seek(-1, SeekOrigin.End);
        return arquivo.ReadByte() == '\n';
    }

    /// <summary>
    /// As linhas do arquivo em texto claro, na ordem. Arquivo ausente não tem linhas; linha que
    /// não abre é pulada, como a linha ilegível sempre foi.
    /// </summary>
    public static IEnumerable<string> Linhas(string caminho)
    {
        if (!File.Exists(caminho)) yield break;

        foreach (string linha in File.ReadLines(caminho, SemBom))
        {
            if (!Abrir(linha, out string claro))
            {
                Console.WriteLine($"[CIFRA] Linha de {Path.GetFileName(caminho)} não abre nesta conta, pulada.");
                continue;
            }

            // Uma linha cifrada pode guardar um arquivo inteiro (GravarTexto).
            if (Cifrada(linha) && claro.Contains('\n'))
                foreach (string parte in claro.Split('\n')) yield return parte.TrimEnd('\r');
            else
                yield return claro;
        }
    }

    // ── Arquivo inteiro ───────────────────────────────────────────────────

    /// <summary>
    /// Grava o arquivo inteiro como uma linha cifrada, por um temporário: uma queda no meio
    /// deixa o arquivo anterior, e não meio arquivo.
    /// </summary>
    public static void GravarTexto(string caminho, string texto)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(caminho)!);
        string temporario = caminho + ".tmp";
        File.WriteAllText(temporario, Cifrar(texto) + Environment.NewLine, SemBom);
        File.Move(temporario, caminho, overwrite: true);
    }

    /// <summary>O arquivo no formato binário, o do <c>profile.dat</c>.</summary>
    public static void Gravar(string caminho, string texto)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(caminho)!);
        File.WriteAllBytes(caminho, ProtectedData.Protect(SemBom.GetBytes(texto), null, DataProtectionScope.CurrentUser));
    }

    /// <summary>
    /// O texto do arquivo, ou null quando ele não existe. Lê os três casos: binário, de linhas
    /// cifradas e texto claro — o de antes da cifra, que quem chama regrava.
    /// </summary>
    public static string? Ler(string caminho)
    {
        if (!File.Exists(caminho)) return null;

        byte[] bytes = File.ReadAllBytes(caminho);
        try
        {
            return SemBom.GetString(ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser));
        }
        catch (CryptographicException)
        {
            // Não é o binário: é texto, cifrado por linha ou claro.
        }

        string texto = SemBom.GetString(bytes).TrimStart((char)0xFEFF);
        if (!texto.Contains(Marca, StringComparison.Ordinal)) return texto;

        return string.Join("\n", Linhas(caminho));
    }
}
