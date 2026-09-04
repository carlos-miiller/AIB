using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AIB.Services.Mail;

/// <summary>Um remetente que dispara rajada a partir de N mensagens numa janela.</summary>
/// <param name="Email">Endereço, minúsculo.</param>
/// <param name="Minimo">A partir de quantas mensagens vira incidente.</param>
/// <param name="Janela">Em quanto tempo.</param>
public sealed record FonteDeAlerta(string Email, int Minimo, TimeSpan Janela);

/// <summary>
/// O <c>regras.md</c> de <c>~/.AIB/email/</c> — do usuário, editável, no espírito do
/// <c>facts.md</c>.
/// <para>
/// Guarda o que só o usuário sabe: quais remetentes são fonte de alerta e a partir de quantas
/// mensagens uma sequência deles vira incidente. Nenhum modelo adivinha isso — o firewall dele
/// manda e-mail a cada oscilação de link, e doze em quarenta minutos foi a coisa mais urgente
/// do dia enquanto uma sozinha não é nada.
/// </para>
/// <para>
/// Formato de uma linha:
/// <code>firewall@empresa.com.br → rajada a partir de 3 em 30 min</code>
/// A seta pode ser <c>→</c> ou <c>-&gt;</c>. Linha que não casa é IGNORADA em silêncio, não
/// derruba o arquivo: é um arquivo que o usuário edita à mão, e um erro de digitação numa linha
/// não pode calar as outras.
/// </para>
/// </summary>
public sealed class RegrasDoVigia
{
    private RegrasDoVigia(IReadOnlyList<FonteDeAlerta> fontes) => Fontes = fontes;

    public IReadOnlyList<FonteDeAlerta> Fontes { get; }

    public static RegrasDoVigia Vazias => new(Array.Empty<FonteDeAlerta>());

    public static string CaminhoPadrao(string? raizDeDados = null) =>
        Path.Combine(
            string.IsNullOrWhiteSpace(raizDeDados) ? DirectoryService.DataDir : raizDeDados!,
            "email", "regras.md");

    public static RegrasDoVigia Ler(string caminho)
    {
        try
        {
            if (!File.Exists(caminho)) return Vazias;
            return Interpretar(File.ReadAllLines(caminho));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[VIGIA] regras.md ilegível ({ex.Message}); seguindo sem regras.");
            return Vazias;
        }
    }

    /// <summary>
    /// Interpreta as linhas. Público e puro para os ensaios: é aqui que o formato erra, não na
    /// leitura do disco.
    /// </summary>
    public static RegrasDoVigia Interpretar(IEnumerable<string> linhas)
    {
        var fontes = new List<FonteDeAlerta>();

        foreach (string bruta in linhas ?? Array.Empty<string>())
        {
            string linha = (bruta ?? "").Trim();
            if (linha.Length == 0 || linha.StartsWith("#")) continue;

            int seta = linha.IndexOf('→');
            int tamanhoDaSeta = 1;
            if (seta < 0) { seta = linha.IndexOf("->", StringComparison.Ordinal); tamanhoDaSeta = 2; }
            if (seta < 0) continue;

            string email = linha.Substring(0, seta).Trim().ToLowerInvariant();
            string regra = linha.Substring(seta + tamanhoDaSeta).Trim().ToLowerInvariant();

            if (email.Length == 0 || !email.Contains('@')) continue;
            if (!regra.Contains("rajada")) continue;

            var numeros = ExtrairNumeros(regra);
            if (numeros.Count < 2) continue;

            int minimo = numeros[0];
            int quantidade = numeros[1];
            if (minimo < 1) continue;

            var janela = regra.Contains(" h") || regra.Contains("hora")
                ? TimeSpan.FromHours(quantidade)
                : TimeSpan.FromMinutes(quantidade);

            if (janela <= TimeSpan.Zero) continue;

            fontes.Add(new FonteDeAlerta(email, minimo, janela));
        }

        return new RegrasDoVigia(fontes);
    }

    public FonteDeAlerta? Fonte(string endereco)
    {
        string chave = (endereco ?? "").Trim().ToLowerInvariant();
        return Fontes.FirstOrDefault(f => f.Email == chave);
    }

    private static List<int> ExtrairNumeros(string texto)
    {
        var numeros = new List<int>();
        int i = 0;

        while (i < texto.Length)
        {
            if (!char.IsDigit(texto[i])) { i++; continue; }

            int fim = i;
            while (fim < texto.Length && char.IsDigit(texto[fim])) fim++;

            if (int.TryParse(texto.Substring(i, fim - i), out int n)) numeros.Add(n);
            i = fim;
        }

        return numeros;
    }
}
