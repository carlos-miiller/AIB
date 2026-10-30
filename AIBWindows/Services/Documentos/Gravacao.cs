using System;
using System.IO;
using System.Security.Cryptography;

namespace AIB.Services.Documentos;

/// <summary>
/// Erro que vai ao modelo como está: diz o que estava errado no pedido (célula inválida, trecho
/// que não existe, aba sem esse nome). Qualquer outra exceção é falha do arquivo ou do programa.
/// </summary>
public sealed class ErroDeDocumento : Exception
{
    public ErroDeDocumento(string mensagem) : base(mensagem) { }
}

/// <summary>
/// Uma gravação preparada: o plano (que o cartão mostra) e como gravá-lo. Preparar não toca o
/// disco; só <see cref="Gravar"/> toca.
/// </summary>
public abstract class Gravacao
{
    protected Gravacao(PlanoDeGravacao plano) => Plano = plano;

    public PlanoDeGravacao Plano { get; }

    /// <summary>O que foi feito, para a resposta ao modelo: "4 células gravadas".</summary>
    public abstract string Resultado { get; }

    /// <summary>
    /// Grava. Primeiro num arquivo temporário na mesma pasta, depois por cima do original: uma
    /// falha no meio (disco cheio, Excel que abriu o arquivo agora) não deixa o original pela
    /// metade.
    /// </summary>
    /// <param name="destino">
    /// Outro caminho no lugar do <see cref="PlanoDeGravacao.Arquivo"/>: o "Salvar como cópia" do
    /// arquivo travado pelo Excel.
    /// </param>
    public void Gravar(string? destino = null)
    {
        string alvo = destino ?? Plano.Arquivo;
        string pasta = Path.GetDirectoryName(alvo) ?? ".";
        Directory.CreateDirectory(pasta);
        string temporario = Path.Combine(pasta, $"~aib-{Guid.NewGuid():N}{Path.GetExtension(alvo)}");

        try
        {
            GravarEm(temporario);
            File.Move(temporario, alvo, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporario)) File.Delete(temporario); } catch { }
        }
    }

    /// <summary>Grava o resultado no caminho dado (um temporário).</summary>
    protected abstract void GravarEm(string caminho);

    /// <summary>O SHA-256 do arquivo, ou null se não existe. Ver <see cref="PlanoDeGravacao.Impressao"/>.</summary>
    public static string? Impressao(string caminho)
    {
        if (!File.Exists(caminho)) return null;
        using var fs = new FileStream(caminho, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexString(SHA256.HashData(fs));
    }
}
