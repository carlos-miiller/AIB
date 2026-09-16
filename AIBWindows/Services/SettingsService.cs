using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Security.Cryptography;
using System.Linq;
using System.Text;

namespace AIB.Services;

public sealed class UserAppSettings
{
    public string ApiUrl { get; set; } = ProvedoresDeIa.UrlDoOllama;
    public string ApiKey { get; set; } = "ollama";
    public string ModelName { get; set; } = "qwen2.5:7b";
    public string ActiveCharacter { get; set; } = "Ayano";
    /// <summary>
    /// keep_alive do Ollama no provedor ATIVO. Nasce "-1" (sempre carregado) porque era o que de fato
    /// valia: a tela oferecia "5 minutos" e o valor nunca chegava à requisição — o provider mandava
    /// -1 sempre. Ver <see cref="Sanear"/> para quem tinha o "5m" gravado.
    /// </summary>
    public string KeepAlive { get; set; } = "-1";
    public string AiProvider { get; set; } = ""; // Vazio por default força a tela de Onboarding
    public string ShadowModelName { get; set; } = "qwen2.5:7b";

    // ─────────────────────────────────────────────────────────────────────
    // Provedor: os campos acima (AiProvider, ApiUrl, ModelName, KeepAlive) e os dois abaixo são
    // o provedor ATIVO da conversa, e continuam sendo o que o resto do programa lê. Perfis guarda
    // os de cada provedor, para trocar e voltar sem perder nada.
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>Janela de contexto do provedor ativo. Ver <see cref="PerfilDeProvedor.JanelaDeContexto"/>.</summary>
    public int ContextWindow { get; set; } = PerfilDeProvedor.JanelaPadrao;

    /// <summary>Raciocínio do provedor ativo. Ver <see cref="PerfilDeProvedor.Raciocinio"/>.</summary>
    public string Reasoning { get; set; } = PerfilDeProvedor.RaciocinioDesligado;

    /// <summary>A configuração de cada provedor, pelo nome. A do ativo é espelho dos campos acima.</summary>
    public Dictionary<string, PerfilDeProvedor> Perfis { get; set; } = new();

    /// <summary>
    /// Provedor da triagem de e-mail. Separado do da conversa de propósito: dá para conversar pelo
    /// OpenRouter e manter a leitura dos e-mails no Ollama, sem mandar trechos deles para fora.
    /// </summary>
    public string MailTriageProvider { get; set; } = ProvedoresDeIa.Ollama;

    /// <summary>Modelo da triagem, no provedor dela. Vazio usa o modelo do perfil daquele provedor.</summary>
    public string MailTriageModel { get; set; } = "";

    /// <summary>Se a migração para perfis por provedor já rodou neste arquivo.</summary>
    public bool PerfisMigrados { get; set; }

    /// <summary>
    /// OpenRouter: só rotear para provedores que não guardam nem treinam com o prompt
    /// (<c>data_collection: deny</c>). NASCE LIGADO: o prompt leva arquivos e e-mails. Desligar
    /// amplia os provedores disponíveis — às vezes mais baratos ou mais rápidos.
    /// </summary>
    public bool OpenRouterSemColetaDeDados { get; set; } = true;

    /// <summary>O perfil do provedor ativo, montado dos campos da conversa.</summary>
    public PerfilDeProvedor PerfilAtivo() => new PerfilDeProvedor
    {
        Url = ApiUrl,
        Modelo = ModelName,
        KeepAlive = KeepAlive,
        JanelaDeContexto = ContextWindow,
        Raciocinio = Reasoning
    }.Sanear(AiProvider);

    /// <summary>O perfil de um provedor: o ativo, o guardado ou o de fábrica.</summary>
    public PerfilDeProvedor PerfilDe(string provedor)
    {
        // Sem provedor gravado (antes do primeiro arranque, ou num ensaio), os campos da conversa
        // são os do Ollama, que é o que a fábrica usa nesse caso.
        string ativo = AiProvider.Length > 0 ? AiProvider : ProvedoresDeIa.Ollama;
        if (provedor == ativo) return PerfilAtivo();
        return Perfis.TryGetValue(provedor, out var guardado)
            ? guardado.Clone().Sanear(provedor)
            : ProvedoresDeIa.PerfilPadrao(provedor);
    }

    /// <summary>Torna <paramref name="provedor"/> o ativo, com <paramref name="perfil"/>, guardando o anterior.</summary>
    public void Ativar(string provedor, PerfilDeProvedor perfil)
    {
        if (AiProvider.Length > 0 && AiProvider != provedor) Perfis[AiProvider] = PerfilAtivo();
        else if (AiProvider.Length == 0 && provedor != ProvedoresDeIa.Ollama) Perfis[ProvedoresDeIa.Ollama] = PerfilAtivo();

        var p = perfil.Clone().Sanear(provedor);
        AiProvider = provedor;
        ApiUrl = p.Url;
        ModelName = p.Modelo;
        KeepAlive = p.KeepAlive;
        ContextWindow = p.JanelaDeContexto;
        Reasoning = p.Raciocinio;
        ModelThinking = p.Raciocinio != PerfilDeProvedor.RaciocinioDesligado;
        Perfis[provedor] = p.Clone();
    }

    /// <summary>
    /// Uma cópia destas configurações com o provedor e o modelo DA TRIAGEM no lugar dos da conversa
    /// — é o que a fábrica de provider recebe quando quem pede é o vigia de e-mail.
    /// </summary>
    public UserAppSettings ParaTriagem()
    {
        var copia = Clone();
        string provedor = ProvedoresDeIa.Normalizar(MailTriageProvider, null);
        if (provedor.Length == 0) provedor = ProvedoresDeIa.Ollama;

        var perfil = PerfilDe(provedor);
        if (!string.IsNullOrWhiteSpace(MailTriageModel)) perfil.Modelo = MailTriageModel.Trim();

        copia.Ativar(provedor, perfil);
        return copia;
    }
    public bool SendSystemPrompt { get; set; } = true;
    public bool EnableIntelligentTools { get; set; } = true;
    // Opt-in: a funcionalidade Shadow Assistant fica desligada por default.
    // Quando ligado, o botão do olho aparece no chat e o usuário decide quando ativar.
    public bool ShadowAssistantEnabled { get; set; } = false;

    /// <summary>
    /// Se o orbe pode ler e triar a caixa de entrada.
    /// <para>
    /// Opt-in pelo mesmo motivo que o orbe: ninguém ganha um programa lendo o próprio e-mail
    /// por ter atualizado. E é uma chave SEPARADA da do orbe de propósito — querer a bola no
    /// desktop não é querer que ela abra a caixa de entrada, e amarrar as duas tiraria do
    /// usuário a única decisão que ele realmente precisa tomar aqui.
    /// </para>
    /// </summary>
    public bool ShadowHandlesMail { get; set; } = false;
    // Quando ativo, o console mostra logs detalhados do streaming ReAct
    // (STREAM-DBG, contadores de updates, classificação de chunks).
    // Útil para diagnosticar respostas vazias ou comportamento estranho do modelo.
    public bool VerboseConsoleLogging { get; set; } = false;

    /// <summary>
    /// Espelha o console num arquivo por execução, em <c>~/.AIB/logs/</c>.
    /// <para>
    /// Nasce desligada. Ligada, ela GRAVA EM DISCO tudo o que sai no terminal — inclusive os
    /// prompts mandados ao modelo, e o da triagem carrega assunto, remetente e corpo dos
    /// e-mails. É o oposto do que a regra 3 do vigia faz no resto do programa, e por isso é
    /// escolha explícita do usuário, com aviso no cabeçalho do próprio arquivo.
    /// </para>
    /// </summary>
    public bool ExecutionLogging { get; set; } = false;

    /// <summary>
    /// Grava o diário da compactação na pasta da sessão, ao lado do <c>raw.jsonl</c>.
    /// <para>
    /// Quando a compactação disparou, quanto o resumidor custou e — o que importa — as vezes em
    /// que ela falhou. Hoje um resumo que estoura o teto imprime uma linha no console e morre
    /// ali; na execução seguinte ninguém sabe que a conversa andou com a poda de emergência.
    /// </para>
    /// <para>
    /// É mais barato que o registro de execução: grava CONTAGENS e tempos, não o texto dos
    /// resumos — esses já ficam em <c>chapters.jsonl</c> e <c>acts.jsonl</c>. Por isso pode
    /// ficar ligado sem virar despejo.
    /// </para>
    /// </summary>
    public bool CompactionLogging { get; set; } = false;
    public string DataDir { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AIB");

    // Diretórios
    public string DataDirectory { get; set; } = "";
    public string TempDirectory { get; set; } = "";

    // Avançado

    /// <summary>
    /// Controla apenas o denylist pós-modal de <c>shell</c>; NÃO controla o modal em si.
    /// Modal sempre dispara em shell (independente desta flag).
    ///
    /// ON (default): o denylist roda como segunda camada após o modal em níveis &lt; 9.
    /// OFF: denylist é ignorado; o modal é o único portão.
    /// L9: denylist sempre ignorado, independente desta flag (D5).
    ///
    /// Migração: perfis legados sem este campo desserializam para o C# default <c>true</c>
    /// automaticamente via <see cref="System.Text.Json.JsonSerializer"/>.
    /// </summary>
    public bool ConfirmDangerousCommands { get; set; } = true;

    /// <summary>
    /// Pastas onde <c>write</c> e <c>edit</c> podem gravar — uma por linha, caminho absoluto.
    /// <para>
    /// Vazio é o comportamento de sempre: o disco inteiro. Confinar é uma escolha do usuário, e
    /// ligá-la sozinha quebraria quem usa a AIB para mexer em projeto fora da pasta pessoal.
    /// </para>
    /// <para>
    /// NÃO alcança o <c>shell</c>: um <c>Set-Content</c> escreve onde quiser, e a defesa dele
    /// continua sendo o portão de confirmação mais a denylist. Ver <see cref="PastasPermitidas"/>.
    /// </para>
    /// </summary>
    public string WriteRoots { get; set; } = "";

    // ─────────────────────────────────────────────────────────────────────
    // Valores que eram constantes no código
    //
    // Cada um traz o padrão como const NOMEADA, e não como literal solto no
    // inicializador. O botão "Restaurar padrões" de cada página, os ensaios e o
    // texto de ajuda leem a MESMA const — três lugares que, com o número
    // digitado em cada um, sairiam de sincronia na primeira mudança.
    // ─────────────────────────────────────────────────────────────────────

    public const int PadraoDeIteracoes = 18;

    /// <summary>
    /// Teto de passos do laço ReAct num turno. Estourar não é sucesso silencioso: o usuário vê
    /// o corte. 18 permite tarefas multi-passo — com 5 já abortava em "ler 6 arquivos antes de
    /// decidir" — e por volta de 20 a coerência do modelo pequeno começa a cair.
    /// </summary>
    public int MaxTurnIterations { get; set; } = PadraoDeIteracoes;

    public const double PadraoDoGatilhoDeCompactacao = 0.85;

    /// <summary>
    /// Fração da cota viva a partir da qual a conversa é compactada. Não é 1,0 de propósito: o
    /// gatilho precisa disparar ANTES do estouro, senão a poda de emergência entra primeiro e
    /// come as mensagens que o capítulo iria resumir.
    /// </summary>
    public double CompactionTrigger { get; set; } = PadraoDoGatilhoDeCompactacao;

    public const double PadraoDaFatiaDeMemoria = 0.25;

    /// <summary>Quanto do contexto disponível é reservado para memória.</summary>
    public double MemoryFraction { get; set; } = PadraoDaFatiaDeMemoria;

    public const int PadraoDeEmailsNoShadow = 3;

    /// <summary>
    /// Quantos e-mails cabem na fala do Shadow antes de o resto virar uma linha de texto. Uma
    /// caixa de entrada inteira flutuando sobre o desktop não é o produto.
    /// </summary>
    public int ShadowMailPreviewCount { get; set; } = PadraoDeEmailsNoShadow;

    public const int PadraoDaJanelaDeEmailEmDias = 3;

    /// <summary>
    /// Quantos dias para trás a varredura olha. Triar backlog é trabalho jogado fora: ninguém
    /// lê 300 pendências de três meses.
    /// </summary>
    public int MailWindowDays { get; set; } = PadraoDaJanelaDeEmailEmDias;

    public const int PadraoDoTempoLimiteImapEmSegundos = 15;

    /// <summary>Quanto se espera por caixa antes de desistir. Servidor mudo não segura a tela.</summary>
    public int MailTimeoutSeconds { get; set; } = PadraoDoTempoLimiteImapEmSegundos;

    public const bool PadraoDoRaciocinio = false;

    /// <summary>
    /// Deixar o modelo raciocinar antes de responder, nas conversas.
    /// <para>
    /// NASCE DESLIGADO por medição, não por gosto. Nesta máquina o Ollama roda 100% em CPU — não
    /// há GPU utilizável —, e a geração fica em 1,1 a 3,1 tokens por segundo. Numa sessão real
    /// de oito turnos, TODOS terminaram com <c>content=0</c>: o modelo só pensou e chamou
    /// ferramenta. Um único turno gastou 953 tokens de raciocínio em 726 segundos — doze minutos
    /// para produzir zero texto na tela.
    /// </para>
    /// <para>
    /// Ligado, o campo <c>think</c> não é enviado e o modelo usa o padrão dele. Desligado, vai
    /// <c>think:false</c> — que é o que a triagem de e-mail e o compactador já faziam por conta
    /// própria, pelo mesmo motivo.
    /// </para>
    /// </summary>
    public bool ModelThinking { get; set; } = PadraoDoRaciocinio;

    public const bool PadraoDaFalaNoHistorico = true;

    /// <summary>
    /// Guardar o que o agente FALOU junto da chamada de ferramenta que ele fez em seguida.
    /// <para>
    /// Nasce LIGADA porque o contrário é um defeito, não uma escolha. O laço gravava só as
    /// <c>tool_calls</c> — a fala "vou abrir o users.xls" era mostrada ao usuário e some do
    /// histórico do próprio modelo. Na iteração seguinte ele via uma chamada e um erro, sem
    /// nenhum registro de por que tinha escolhido aquele caminho, e repetia a mesma chamada.
    /// Foram quatro repetições idênticas numa sessão real.
    /// </para>
    /// <para>
    /// A chave existe para poder desligar se algum modelo reagir mal, não porque desligado seja
    /// um estado desejável.
    /// </para>
    /// </summary>
    public bool KeepAssistantSpeech { get; set; } = PadraoDaFalaNoHistorico;

    public const bool PadraoDoRaciocinioNoHistorico = false;

    /// <summary>
    /// Devolver o bloco de raciocínio ao modelo nas idas seguintes, em vez de apará-lo.
    /// <para>
    /// NASCE DESLIGADA e é a mais incerta das três. Modelos de raciocínio são treinados
    /// esperando o bloco de pensamento AUSENTE do histórico; devolvê-lo vai contra o treino e
    /// pode degradar em vez de melhorar. Por isso é chave, e não padrão.
    /// </para>
    /// <para>
    /// O custo é menor do que parece: histórico só cresce no fim, então o cache de prefixo
    /// continua valendo e os tokens extras são prefilados UMA vez, não a cada iteração.
    /// </para>
    /// <para>
    /// Sem <see cref="ModelThinking"/> não há raciocínio nenhum, e esta chave não faz diferença.
    /// </para>
    /// </summary>
    public bool ThinkingInHistory { get; set; } = PadraoDoRaciocinioNoHistorico;

    public const bool PadraoDoRaciocinioNaTriagem = false;

    /// <summary>
    /// Deixar o modelo raciocinar antes de classificar cada e-mail.
    /// <para>
    /// É o lugar onde raciocínio tem mais chance de pagar: a triagem é julgamento de tiro único
    /// — "isto pede ação hoje?" — e não uma sequência de ferramentas. E é o único mensurável
    /// objetivamente: o diário grava a urgência de cada mensagem, então ligar e contar quantas
    /// mala-diretas caem em "media" é número, não impressão.
    /// </para>
    /// <para>
    /// Nasce desligada por herança da medição do compactador: resumir cinco turnos custava
    /// 286,6s, dos quais 226,7s eram raciocínio para um resumo de 117 tokens. Com o raciocínio
    /// desligado, 14,7s. Se aqui for diferente, o diário dirá.
    /// </para>
    /// </summary>
    public bool MailTriageThinking { get; set; } = PadraoDoRaciocinioNaTriagem;

    public const int PadraoDeDiasDeDiario = 7;

    /// <summary>
    /// Por quantos dias o que a triagem decidiu fica gravado, para a conversa poder consultar.
    /// <para>
    /// É o mostrador da regra 3. Em ZERO, nada do e-mail toca o disco e a ferramenta
    /// <c>mail</c> não tem o que responder — a conversa volta a não saber nada
    /// sobre a caixa, que era o comportamento original. Acima de zero, ficam gravados
    /// remetente, assunto e o resumo de uma frase, que são os mesmos campos que já apareciam na
    /// tela do Shadow e no painel.
    /// </para>
    /// <para>
    /// O CORPO da mensagem nunca é gravado, em nenhum valor deste campo. Isso não é
    /// configurável, e é o que sobrou da regra 3 como regra.
    /// </para>
    /// </summary>
    /// <summary>
    /// Retenção do que a TRIAGEM AUTOMÁTICA leu: o diário e o arquivo por conversa. Zero
    /// apaga tudo e devolve a regra 3 estrita.
    /// <para>
    /// NÃO governa a conversa que o usuário abre sobre um e-mail (§3.11). São atos diferentes:
    /// um é a máquina lendo correspondência sozinha, o outro é uma pessoa decidindo conversar.
    /// Pôr os dois no mesmo relógio apagaria conversas do usuário por decurso de prazo — o que
    /// não acontece com nenhuma outra conversa do app.
    /// </para>
    /// <para>
    /// E não adiantaria: o <c>raw.jsonl</c> guarda o mesmo texto e, por regra do projeto, nunca
    /// é apagado. Amarrar só o <c>chat_history.json</c> aqui apagaria UMA das duas cópias e
    /// faria o mostrador parecer completo sem ser. Apagar pela metade é pior que não apagar:
    /// cria confiança falsa exatamente onde ela custa caro.
    /// </para>
    /// </summary>
    public int MailJournalDays { get; set; } = PadraoDeDiasDeDiario;

    // Gamificação / Sistema de Níveis
    public int MessageCount { get; set; } = 0;

    /// <summary>
    /// Caixas de e-mail configuradas — tela-configuracoes §3.12, uma linha da lista por item.
    /// <para>
    /// SEM SENHA. A senha de app de cada caixa vive no <c>MailVault</c>, indexada pelo
    /// endereço, e nunca passa por aqui: este objeto é serializado em JSON e clonado a cada
    /// <c>LoadSettings</c>, dois caminhos por onde uma senha não deve trafegar.
    /// </para>
    /// </summary>
    public List<MailAccountSettings> MailAccounts { get; set; } = new();

    /// <summary>
    /// Cópia para entregar a quem pediu as configurações.
    /// <para>
    /// Era cópia rasa, e a justificativa era "todos os campos são string ou tipo de valor".
    /// Deixou de valer com <see cref="MailAccounts"/>: o <c>MemberwiseClone</c> copia a
    /// REFERÊNCIA da lista, e como o <c>LoadSettings</c> devolve um clone do cache, quem
    /// mexesse na lista recebida estaria mexendo na lista do cache — e na das outras janelas.
    /// </para>
    /// </summary>
    /// <summary>
    /// Põe os números dentro de faixas em que o programa ainda funciona.
    /// <para>
    /// O arquivo é editável à mão e agora carrega valores que, errados, quebram coisas de
    /// verdade: fração de memória em 0,99 não deixa espaço para conversar, gatilho de
    /// compactação em 0 compacta a cada turno, zero iterações não roda turno nenhum. Corrigir
    /// aqui, na entrada, é mais barato que espalhar defesa por cada consumidor — e nenhum deles
    /// tem contexto para saber o que fazer com um valor absurdo.
    /// </para>
    /// <para>
    /// SANEIA, não recusa: um número fora da faixa vira o mais próximo válido, e o usuário
    /// perde o exagero, não as configurações inteiras.
    /// </para>
    /// </summary>
    public UserAppSettings Sanear()
    {
        SanearProvedores();

        MaxTurnIterations = Entre(MaxTurnIterations, 1, 60);
        CompactionTrigger = Entre(CompactionTrigger, 0.50, 0.99);
        MemoryFraction = Entre(MemoryFraction, 0.05, 0.60);
        ShadowMailPreviewCount = Entre(ShadowMailPreviewCount, 1, 10);
        MailWindowDays = Entre(MailWindowDays, 1, 30);
        MailTimeoutSeconds = Entre(MailTimeoutSeconds, 5, 120);
        MailJournalDays = Entre(MailJournalDays, 0, 90);
        return this;
    }

    private static int Entre(int valor, int minimo, int maximo) =>
        valor < minimo ? minimo : valor > maximo ? maximo : valor;

    private static double Entre(double valor, double minimo, double maximo) =>
        double.IsNaN(valor) || valor < minimo ? minimo : valor > maximo ? maximo : valor;

    /// <summary>
    /// Provedor ativo, perfis e triagem dentro do que existe.
    /// <para>
    /// A MIGRAÇÃO roda uma vez por arquivo. Três coisas do arquivo antigo não querem dizer o que
    /// parecem: o provedor pode ser "OpenAI", "Anthropic" ou "LmStudio", que a AIB não fala mais;
    /// o keep-alive "5m" nunca chegou ao Ollama, que recebia -1 sempre; e a triagem de e-mail
    /// sempre usou o provedor e o modelo da conversa. A migração preserva o que de fato
    /// acontecia, e não o que a tela dizia.
    /// </para>
    /// </summary>
    private void SanearProvedores()
    {
        string anterior = AiProvider ?? "";
        AiProvider = ProvedoresDeIa.Normalizar(anterior, ApiUrl);

        // Provedor antigo que virou Ollama com uma URL de nuvem: a URL não serve a ele.
        if (AiProvider == ProvedoresDeIa.Ollama && anterior != ProvedoresDeIa.Ollama
            && !(ApiUrl ?? "").Contains("127.0.0.1") && !(ApiUrl ?? "").Contains("localhost"))
            ApiUrl = ProvedoresDeIa.UrlDoOllama;

        if (!PerfisMigrados)
        {
            if (KeepAlive == "5m") KeepAlive = "-1";
            Reasoning = ModelThinking ? PerfilDeProvedor.RaciocinioDoModelo : PerfilDeProvedor.RaciocinioDesligado;
            if (ContextWindow <= 0) ContextWindow = PerfilDeProvedor.JanelaPadrao;

            MailTriageProvider = AiProvider.Length > 0 ? AiProvider : ProvedoresDeIa.Ollama;
            MailTriageModel = ModelName ?? "";

            PerfisMigrados = true;
        }

        Perfis ??= new();

        // Perfis de provedor que não existe mais saem; os que ficam, saneados.
        foreach (var nome in Perfis.Keys.ToList())
        {
            if (!ProvedoresDeIa.Todos.Contains(nome)) Perfis.Remove(nome);
            else Perfis[nome] = (Perfis[nome] ?? ProvedoresDeIa.PerfilPadrao(nome)).Sanear(nome);
        }

        if (AiProvider.Length > 0)
        {
            // Os campos da conversa são a verdade do ativo; o perfil guardado os acompanha.
            var ativo = PerfilAtivo();
            ApiUrl = ativo.Url;
            KeepAlive = ativo.KeepAlive;
            ContextWindow = ativo.JanelaDeContexto;
            Reasoning = ativo.Raciocinio;
            ModelThinking = ativo.Raciocinio != PerfilDeProvedor.RaciocinioDesligado;
            Perfis[AiProvider] = ativo;
        }

        string triagem = ProvedoresDeIa.Normalizar(MailTriageProvider, null);
        MailTriageProvider = triagem.Length > 0 ? triagem : ProvedoresDeIa.Ollama;
        MailTriageModel ??= "";
    }

    public UserAppSettings Clone()
    {
        var copia = (UserAppSettings)MemberwiseClone();
        copia.MailAccounts = MailAccounts.Select(c => c.Clone()).ToList();
        copia.Perfis = (Perfis ?? new()).ToDictionary(kv => kv.Key, kv => kv.Value.Clone());
        return copia;
    }
}

/// <summary>
/// O que de uma caixa de e-mail vai para o disco — §9 passo 2 de tela-configuracoes.
/// <para>
/// <c>Status</c> e <c>StatusText</c> NÃO estão aqui de propósito: são de tempo de execução.
/// Gravar "Conectada" e reler isso na abertura seguinte seria afirmar na tela algo que
/// ninguém verificou desde a sessão passada.
/// </para>
/// </summary>
public sealed class MailAccountSettings
{
    public string Address { get; set; } = "";
    public string ImapHost { get; set; } = "";
    public int ImapPort { get; set; } = 993;
    public bool UseSsl { get; set; } = true;
    public bool IsPrimary { get; set; }

    public MailAccountSettings Clone() => (MailAccountSettings)MemberwiseClone();
}

public sealed class SettingsService
{
    // Um HttpClient por processo: a classe era instanciada várias vezes e alocava um em cada.
    private static readonly HttpClient _httpClient = new HttpClient();

    private readonly string? _explicitPath;
    private readonly object _gate = new();
    private UserAppSettings? _cache;
    private string? _cachedPath;

    /// <summary>Usa o caminho corrente do <see cref="DirectoryService"/>, resolvido a cada operação.</summary>
    public SettingsService() : this(null) { }

    /// <summary>Caminho fixo — usado por testes e por perfis alternativos.</summary>
    public SettingsService(string? settingsPath)
    {
        _explicitPath = string.IsNullOrWhiteSpace(settingsPath) ? null : settingsPath;
    }

    // Resolvido a cada operação de IO. O antigo 'static readonly' congelava o caminho no
    // carregamento do tipo, e por isso DirectoryService.ApplyFromSettings nunca conseguia
    // de fato realocar o arquivo de settings.
    private string ResolvePath() => _explicitPath ?? DirectoryService.SettingsPath;

    /// <summary>Caminho efetivo neste momento.</summary>
    public string SettingsPath => ResolvePath();

    /// <summary>Settings do disco, servidas do cache em memória. Devolve sempre uma cópia.</summary>
    public UserAppSettings LoadSettings()
    {
        string path = ResolvePath();

        lock (_gate)
        {
            if (_cache != null && _cachedPath == path)
                return _cache.Clone();
        }

        var loaded = ReadFromDisk(path);
        PastasPermitidas.Configurar(loaded.WriteRoots);
        Ai.ChatRequestOptions.JanelaAtual = loaded.ContextWindow;

        lock (_gate)
        {
            _cache = loaded;
            _cachedPath = path;
            return _cache.Clone();
        }
    }

    /// <summary>Descarta o cache. O próximo <see cref="LoadSettings"/> volta ao disco.</summary>
    public void InvalidateCache()
    {
        lock (_gate)
        {
            _cache = null;
            _cachedPath = null;
        }
    }

    private UserAppSettings ReadFromDisk(string path)
    {
        if (!File.Exists(path))
        {
            var defaults = new UserAppSettings();
            SaveSettings(defaults);
            return defaults;
        }

        try
        {
            byte[] encryptedBytes = File.ReadAllBytes(path);
            byte[] decryptedBytes = ProtectedData.Unprotect(encryptedBytes, null, DataProtectionScope.CurrentUser);
            string json = Encoding.UTF8.GetString(decryptedBytes);
            return (JsonSerializer.Deserialize<UserAppSettings>(json) ?? new UserAppSettings()).Sanear();
        }
        catch
        {
            // Fallback para arquivo em texto claro (Migração de legado)
            try
            {
                string json = File.ReadAllText(path);
                var settings = (JsonSerializer.Deserialize<UserAppSettings>(json)
                                ?? new UserAppSettings()).Sanear();
                SaveSettings(settings); // Re-salva criptografado
                return settings;
            }
            catch
            {
                return new UserAppSettings();
            }
        }
    }

    public void SaveSettings(UserAppSettings settings)
    {
        string path = ResolvePath();
        string json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
        byte[] plainBytes = Encoding.UTF8.GetBytes(json);
        byte[] encryptedBytes = ProtectedData.Protect(plainBytes, null, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(path, encryptedBytes);

        // O confinamento passa a valer no mesmo instante em que o usuario salva. Sincronizar aqui,
        // e nao em cada tela, e o que impede o caso "mudei nas configuracoes e a ferramenta
        // continuou com a lista velha".
        PastasPermitidas.Configurar(settings.WriteRoots);

        // A janela vale no mesmo instante, pelo mesmo motivo: os orçamentos por nível e o teto
        // da poda são lidos dela, e continuar contando a antiga até reabrir o app seria mentir.
        Ai.ChatRequestOptions.JanelaAtual = settings.ContextWindow;

        lock (_gate)
        {
            _cache = settings.Clone();
            _cachedPath = path;
        }
    }

    public async Task<List<string>> GetOllamaModelsAsync(string baseUrl)
    {
        string baseOllamaUrl = string.IsNullOrEmpty(baseUrl) ? "http://localhost:11434" : baseUrl;
        baseOllamaUrl = baseOllamaUrl.Replace("/v1", "").TrimEnd('/');
        
        try
        {
            var response = await _httpClient.GetAsync($"{baseOllamaUrl}/api/tags");
            if (!response.IsSuccessStatusCode) return new List<string>();

            string json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            
            var models = new List<string>();
            if (doc.RootElement.TryGetProperty("models", out JsonElement modelsArray))
            {
                foreach (var model in modelsArray.EnumerateArray())
                {
                    if (model.TryGetProperty("name", out JsonElement nameProp))
                    {
                        models.Add(nameProp.GetString() ?? "");
                    }
                }
            }
            return models;
        }
        catch
        {
            return new List<string>(); // Falha (provavelmente não é Ollama ou está offline)
        }
    }
}
