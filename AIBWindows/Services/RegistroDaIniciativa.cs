using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace AIB.Services;

/// <summary>
/// O diário da iniciativa: por que a persona falou, e — o que importa — por que NÃO falou.
/// <para>
/// Visto no uso: um dia inteiro sem ela puxar assunto, e a suspeita de que a resposta dela a uma
/// mensagem ficava marcada como "não respondida". O estado gravado mostrava que não, mas não
/// havia como dizer quantos sorteios tinham acontecido nem o que segurou os outros: o programa
/// só escrevia uma linha quando ela falava.
/// </para>
/// <para>
/// Grava NÚMEROS e motivos, nunca o texto das mensagens. Fica em <c>~/.AIB/logs/iniciativa.log</c>;
/// passando de <see cref="Teto"/>, o arquivo vira <c>iniciativa.anterior.log</c> e começa outro.
/// </para>
/// <para>
/// NUNCA lança. Diagnóstico que derruba a batida seria pior que a falta dele.
/// </para>
/// </summary>
public sealed class RegistroDaIniciativa
{
    public const string NomeDoArquivo = "iniciativa.log";
    public const string NomeDoAnterior = "iniciativa.anterior.log";

    /// <summary>O tamanho em que o arquivo roda. Uma linha por sorteio dá uns 15 KB por dia.</summary>
    public const long Teto = 1024 * 1024;

    private static readonly UTF8Encoding SemBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly Func<bool> _ligado;
    private readonly string _pasta;
    private readonly object _gate = new();
    private string? _ultimoEstado;

    /// <param name="ligado">A chave das configurações, lida a cada escrita.</param>
    /// <param name="pasta">Pasta alternativa. Existe para o teste não escrever no ~/.AIB real.</param>
    public RegistroDaIniciativa(Func<bool> ligado, string? pasta = null)
    {
        _ligado = ligado ?? throw new ArgumentNullException(nameof(ligado));
        _pasta = pasta ?? Path.Combine(DirectoryService.DataDir, "logs");
    }

    public string Caminho => Path.Combine(_pasta, NomeDoArquivo);

    public bool Ligado
    {
        get { try { return _ligado(); } catch { return false; } }
    }

    /// <summary>Uma linha, com dia e hora locais.</summary>
    public void Escrever(string rotulo, string texto)
    {
        if (!Ligado) return;

        try
        {
            lock (_gate)
            {
                Directory.CreateDirectory(_pasta);
                Rodar();

                string hora = DateTime.Now.ToString("dd/MM HH:mm:ss", CultureInfo.InvariantCulture);
                File.AppendAllText(Caminho, $"{hora}  {rotulo,-12}{texto}{Environment.NewLine}", SemBom);
            }
        }
        catch (Exception ex)
        {
            // Só o console: chamar Escrever de novo aqui daria um laço.
            Console.WriteLine($"[INICIATIVA] não consegui gravar o diário: {ex.Message}");
        }
    }

    /// <summary>
    /// O que está segurando o sorteio agora, ou que ele está livre. A batida é de 20 em 20 s:
    /// só a MUDANÇA vira linha, senão o arquivo seria "ausente" repetido o dia inteiro.
    /// </summary>
    public void Estado(string estado)
    {
        lock (_gate)
        {
            if (estado == _ultimoEstado) return;
            _ultimoEstado = estado;
        }
        Escrever("ESTADO", estado);
    }

    private void Rodar()
    {
        var arquivo = new FileInfo(Caminho);
        if (!arquivo.Exists || arquivo.Length < Teto) return;

        File.Move(Caminho, Path.Combine(_pasta, NomeDoAnterior), overwrite: true);
    }

    private static readonly CultureInfo PtBr = new("pt-BR");

    /// <summary>"4,2%".</summary>
    public static string Pct(double p) => p.ToString("P1", PtBr).Replace(" ", "");

    /// <summary>"2,44".</summary>
    public static string Num(double n) => n.ToString("0.00", PtBr);

    /// <summary>"14:32" local, de um instante UTC.</summary>
    public static string Hora(DateTime utc) => utc.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);
}
