using System.Text.RegularExpressions;

namespace AIB.Services.Memory;

/// <summary>
/// Reconhece, num comando já executado, que ele APAGOU algo — para a memória contar o fato
/// sem entregar o comando pronto para ser repetido.
/// <para>
/// Existe por um incidente repetido. Um capítulo guardou como artefato
/// <c>executou Remove-Item "…\emails fisio" -Recurse -Force …</c>, e o bloco de memória dizia
/// que os artefatos "podem ser usados como estão". Num turno seguinte, querendo "limpar e
/// reexecutar", o modelo copiou o comando byte a byte e apagou de novo a pasta com o template,
/// o CSV e o próprio script que estava corrigindo.
/// </para>
/// <para>
/// Não é o portão de segurança — esse é o cartão de confirmação e o <c>CommandFloorList</c>.
/// Aqui só se decide o que a MEMÓRIA mostra. Errar para o lado de reconhecer demais custa uma
/// linha menos literal no resumo; errar para o outro lado custa uma pasta.
/// </para>
/// </summary>
public static class ComandoQueApaga
{
    private static readonly Regex Apagar = new(
        @"(?<=^|[\s;|&(""'])(remove-item|rm|rmdir|rd|del|erase|ri|clear-content|clear-recyclebin|format-volume|clear-disk)(?=\s|$|;)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex PrimeiroCaminhoEntreAspas = new(
        @"[""']([A-Za-z]:\\[^""']+|\\\\[^""']+|[.~][\\/][^""']*)[""']",
        RegexOptions.Compiled);

    private static readonly Regex PrimeiroCaminhoSolto = new(
        @"(?<![\w""'])([A-Za-z]:\\\S+)",
        RegexOptions.Compiled);

    /// <summary>Se o comando apaga algo.</summary>
    public static bool Eh(string? comando) =>
        !string.IsNullOrWhiteSpace(comando) && Apagar.IsMatch(comando);

    /// <summary>O caminho que o comando apagou, se der para ler; senão, nulo.</summary>
    public static string? Alvo(string comando)
    {
        var m = PrimeiroCaminhoEntreAspas.Match(comando);
        if (m.Success) return m.Groups[1].Value;

        m = PrimeiroCaminhoSolto.Match(comando);
        return m.Success ? m.Groups[1].Value.TrimEnd(';', ',') : null;
    }

    /// <summary>
    /// Como o fato entra na memória: o que foi apagado, e que já está feito. Sem a linha de
    /// comando.
    /// </summary>
    public static string Descrever(string comando) =>
        Alvo(comando) is string alvo
            ? $"apagou {alvo} (já feito — não repetir sem o usuário pedir)"
            : "rodou um comando que apaga arquivos (já feito — não repetir sem o usuário pedir)";
}
