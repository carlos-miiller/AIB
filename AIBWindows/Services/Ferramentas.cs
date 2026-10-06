namespace AIB.Services;

/// <summary>
/// Os nomes das ferramentas nativas, num lugar só, e como cada uma se anuncia na tela.
/// <para>
/// Os nomes estavam espalhados como literais por seis arquivos — extrator de artefato, ícones,
/// card de confirmação, laço do agente e as próprias ferramentas. Renomear uma exigia achar
/// todas as cópias, e uma esquecida não quebra a compilação: vira um <c>switch</c> que cai no
/// default e uma ação sem ícone.
/// </para>
/// <para>
/// Os nomes são CURTOS e em inglês porque é o que o modelo lê e escreve a cada chamada — e
/// porque é a convenção que as ferramentas agênticas de hoje convergiram para: <c>read</c>,
/// <c>write</c>, <c>edit</c>, <c>glob</c>, <c>grep</c>. O que o USUÁRIO lê é o rótulo, que é
/// português e diz o que está acontecendo, não como a função se chama.
/// </para>
/// </summary>
public static class Ferramentas
{
    public const string Ler = "read";
    public const string Gravar = "write";
    public const string Editar = "edit";
    public const string Procurar = "glob";
    public const string Buscar = "grep";
    public const string Shell = "shell";
    public const string Habilidade = "skill";
    public const string Email = "mail";
    public const string LerEmail = "mail_read";
    public const string Arquivos = "fs";
    public const string Navegador = "browser";
    public const string Lembrar = "remember";
    public const string Lembrete = "remind";

    /// <summary>
    /// O que aparece na trilha de ações enquanto a ferramenta roda.
    /// <para>
    /// Gerúndio, e não o nome da função: quem olha a tela quer saber o que está acontecendo
    /// agora. "read" é endereço; "Lendo arquivo" é notícia.
    /// </para>
    /// <para>
    /// Nome desconhecido devolve o próprio nome. Habilidade instalada pelo usuário não tem
    /// rótulo cadastrado aqui, e mostrar o nome dela é melhor que mostrar vazio.
    /// </para>
    /// </summary>
    public static string Rotulo(string? nome) => (nome ?? "").Trim().ToLowerInvariant() switch
    {
        Ler => "Lendo arquivo",
        Gravar => "Gravando arquivo",
        Editar => "Editando arquivo",
        Procurar => "Procurando arquivos",
        Buscar => "Buscando no conteúdo",
        Shell => "Executando comando",
        Habilidade => "Executando habilidade",
        Email => "Consultando e-mails",
        LerEmail => "Lendo o e-mail",
        Arquivos => "Mexendo em arquivos",
        Navegador => "Navegando",
        Lembrar => "Guardando na memória",
        Lembrete => "Agendando lembrete",
        _ => nome ?? ""
    };
}
