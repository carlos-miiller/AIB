using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace AIB.Services.Memory;

/// <summary>
/// A passagem dos arquivos de conversa e de memória para a cifra, e o caminho de volta.
/// <para>
/// MIGRAR regrava cifrado o que ainda está em texto claro — os arquivos de antes da cifra. É a
/// única vez em que o AIB reescreve um <c>raw.jsonl</c>, e por isso confere antes de trocar: o
/// arquivo novo, aberto, tem de dar as mesmas linhas do antigo. Se não der, o antigo fica.
/// </para>
/// <para>
/// EXPORTAR existe porque a cifra é da conta do Windows: reinstalado o sistema ou trocada a
/// máquina, nada abre. A cópia em texto claro, onde o usuário escolher, é o backup dele.
/// </para>
/// </summary>
public static class CifraDaMemoria
{
    private static readonly UTF8Encoding SemBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Arquivos de linhas, em que só se acrescenta: cada linha é cifrada em separado.</summary>
    private static readonly string[] DeLinhas = { "raw.jsonl", "chapters.jsonl", "acts.jsonl", "facts.md", "facts.index.jsonl" };

    /// <summary>Arquivos regravados inteiros: viram uma linha cifrada só.</summary>
    private static readonly string[] Inteiros = { "turno-aberto.json" };

    /// <summary>Os arquivos cifrados da pasta de dados que existem agora.</summary>
    public static IReadOnlyList<string> Arquivos(string dados)
    {
        var achados = new List<string>();

        foreach (string solto in new[] { "chat_history.json", "lembretes.json" })
        {
            string caminho = Path.Combine(dados, solto);
            if (File.Exists(caminho)) achados.Add(caminho);
        }

        string memoria = Path.Combine(dados, "memory");
        if (Directory.Exists(memoria))
            achados.AddRange(Directory.EnumerateFiles(memoria, "*", SearchOption.AllDirectories)
                .Where(a => DeLinhas.Contains(Path.GetFileName(a), StringComparer.OrdinalIgnoreCase)
                            || Inteiros.Contains(Path.GetFileName(a), StringComparer.OrdinalIgnoreCase)));

        return achados;
    }

    private static bool EhDeLinhas(string caminho) =>
        DeLinhas.Contains(Path.GetFileName(caminho), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Cifra o que ainda está em texto claro. Devolve quantos arquivos mudaram. Nunca lança:
    /// arquivo que falha fica como estava, e o AIB continua lendo-o em claro.
    /// </summary>
    public static int Migrar(string? dados = null)
    {
        int mudaram = 0;

        foreach (string caminho in Arquivos(dados ?? DirectoryService.DataDir))
        {
            try
            {
                if (Cifrar(caminho)) mudaram++;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[CIFRA] {caminho} ficou como estava: {ex.Message}");
            }
        }

        if (mudaram > 0) Console.WriteLine($"[CIFRA] {mudaram} arquivo(s) de memória passaram a ser cifrados.");
        return mudaram;
    }

    /// <summary>Regrava um arquivo cifrado, se ele tem texto claro. Verdadeiro quando mudou.</summary>
    private static bool Cifrar(string caminho)
    {
        // O caso de todo arranque depois do primeiro: tudo cifrado, nada a fazer.
        if (File.ReadLines(caminho, SemBom).All(l => l.Length == 0 || ArquivoCifrado.Cifrada(l))) return false;

        string[] antes = File.ReadAllLines(caminho, SemBom);

        string temporario = caminho + ".cifrando";
        if (File.Exists(temporario)) File.Delete(temporario);

        if (EhDeLinhas(caminho))
        {
            // Linha a linha, e a que já era cifrada passa intacta: o arquivo pode ter as duas.
            File.WriteAllLines(temporario, antes.Where(l => l.Length > 0)
                .Select(l => ArquivoCifrado.Cifrada(l) ? l : ArquivoCifrado.Cifrar(l)), SemBom);
        }
        else
        {
            File.WriteAllText(temporario, ArquivoCifrado.Cifrar(File.ReadAllText(caminho, SemBom).TrimStart((char)0xFEFF))
                                          + Environment.NewLine, SemBom);
        }

        // A conferência: aberto, o novo tem de dar o que o antigo dava.
        var esperado = ArquivoCifrado.Linhas(caminho).Where(l => l.Length > 0).ToList();
        var obtido = ArquivoCifrado.Linhas(temporario).Where(l => l.Length > 0).ToList();
        if (!esperado.SequenceEqual(obtido, StringComparer.Ordinal))
        {
            File.Delete(temporario);
            throw new InvalidDataException("o arquivo cifrado não abriu igual ao original");
        }

        File.Move(temporario, caminho, overwrite: true);
        return true;
    }

    /// <summary>
    /// Copia a memória em TEXTO CLARO para <paramref name="destino"/>, com a mesma árvore de
    /// pastas. Devolve quantos arquivos saíram. Não mexe nos originais.
    /// </summary>
    public static int Exportar(string destino, string? dados = null)
    {
        string raiz = dados ?? DirectoryService.DataDir;
        int copiados = 0;

        foreach (string caminho in Arquivos(raiz))
        {
            string relativo = Path.GetRelativePath(raiz, caminho);
            string alvo = Path.Combine(destino, relativo);
            Directory.CreateDirectory(Path.GetDirectoryName(alvo)!);

            string texto = string.Join(Environment.NewLine, ArquivoCifrado.Linhas(caminho));
            File.WriteAllText(alvo, texto + Environment.NewLine, SemBom);
            copiados++;
        }

        return copiados;
    }
}
