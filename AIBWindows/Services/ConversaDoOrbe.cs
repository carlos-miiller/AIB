using System;
using System.Threading;
using System.Threading.Tasks;

namespace AIB.Services;

/// <summary>
/// A conversa do orbe: uma só, que nunca termina, separada da conversa da janela.
/// <para>
/// O orbe rodava o turno ESCONDIDO dentro da conversa principal: o que se dizia pela barra
/// aparecia misturado com o trabalho da janela, e entrava no histórico do painel. Agora ele tem
/// a própria conversa (<see cref="ConversationService"/> em sessão fixa,
/// <c>memory/shadow</c>), fora do histórico; as duas se encontram só nos fatos duráveis.
/// </para>
/// <para>
/// Ela se mantém leve pela compactação: a de sempre (contexto cheio) e mais uma por tempo —
/// parada há <see cref="Pausa"/>, o que estiver solto vira capítulo. A conversa do orbe é feita
/// de rajadas curtas ao longo do dia, que sozinhas quase nunca enchem o contexto; sem isto ela
/// acumularia turnos crus até encher, e cada requisição custaria o dia inteiro.
/// </para>
/// <para>
/// O turno roda aqui, sem janela: o laço de stream da conversa principal desenha cadeia e
/// balões, e o orbe só precisa do passo (anel e ícone) e do texto final. A confirmação das
/// ferramentas segue pelo portão de sempre, que no modo orbe abre a janela perto dele.
/// </para>
/// </summary>
public sealed class ConversaDoOrbe
{
    /// <summary>
    /// A pasta da conversa do orbe, direto na raiz da memória (<c>memory/shadow</c>), e não em
    /// <c>sessions/</c>: é uma só, não uma sessão entre muitas. "shadow", o nome oficial (Shadow
    /// Assistant); "orbe" é o apelido.
    /// </summary>
    public const string Sessao = "shadow";

    /// <summary>Parada há mais que isso, o que estiver solto vira capítulo.</summary>
    public static readonly TimeSpan Pausa = TimeSpan.FromHours(2);

    private readonly SettingsService _settings;
    private bool _compactarNaPausa;

    public ConversaDoOrbe(ConversationService conversa, SettingsService settings)
    {
        Conversa = conversa ?? throw new ArgumentNullException(nameof(conversa));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    public ConversationService Conversa { get; }

    /// <summary>Se há turno rodando.</summary>
    public bool Ocupada { get; private set; }

    /// <summary>Última vez que alguém falou nesta conversa: o usuário ou ela.</summary>
    public DateTime? UltimaAtividadeUtc { get; private set; }

    /// <summary>O passo do turno: rótulo e ferramenta (nula quando é pensar). Vazio no fim.</summary>
    public event Action<string, string?>? PassoMudou;

    /// <summary>O texto final do turno, sem marca de quebra.</summary>
    public event Action<string>? Respondeu;

    /// <summary>Um turno do usuário terminou: o que ele escreveu nele.</summary>
    public event Action<string>? UsuarioFalou;

    private int Nivel => LevelService.GetLevel(_settings.LoadSettings().MessageCount);

    private bool _parou;

    /// <summary>
    /// Para o turno em andamento, a pedido do usuário. O turno fecha como cancelado (a conversa
    /// cuida disso) e a barra recebe o que já tinha sido dito, ou "Parei.".
    /// </summary>
    public void Parar()
    {
        if (!Ocupada) return;

        _parou = true;
        Conversa.CancelGeneration();

        // O /compact não é um turno: quem o interrompe é a desistência da compactação.
        Conversa.InterromperCompactacao();
    }

    /// <summary>Os comandos de barra que o orbe entende.</summary>
    public enum Comando { Nenhum, Compactar, Memoria }

    /// <summary>
    /// O comando que o texto da barra é, se for um. Os mesmos da janela de chat que fazem
    /// sentido aqui, mais <c>/memory</c>, que é como o usuário pediu.
    /// </summary>
    public static Comando ComandoDe(string? texto) => (texto ?? "").Trim().ToLowerInvariant() switch
    {
        "/compact" => Comando.Compactar,
        "/memoria" or "/memória" or "/memory" => Comando.Memoria,
        _ => Comando.Nenhum
    };

    /// <summary>
    /// Roda um comando de barra na conversa DO ORBE. Não passa pelo modelo como mensagem: no
    /// orbe o <c>/compact</c> ia ao modelo como texto comum, e ele respondia a ele.
    /// <para>
    /// Não conta como conversa: não dispara <see cref="UsuarioFalou"/> (o afeto e o contato não
    /// andam por um comando) nem marca atividade nova para a compactação da pausa.
    /// </para>
    /// </summary>
    private async Task RodarComandoAsync(Comando comando)
    {
        Ocupada = true;
        _parou = false;
        string resposta;

        try
        {
            if (comando == Comando.Memoria)
            {
                // Entre cercas: a conta é alinhada por espaços, e o Markdown juntaria as colunas.
                resposta = "```\n" + Conversa.MemoriaEmTexto(Nivel) + "\n```";
            }
            else
            {
                PassoMudou?.Invoke("Compactando", null);
                resposta = await Conversa.ForcarCompactacaoAsync(Nivel).ConfigureAwait(true);

                // O que havia de novo acabou de ser compactado: a pausa não pede de novo.
                _compactarNaPausa = false;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ORBE] comando falhou: {ex.Message}");
            resposta = $"O comando falhou: {ex.Message}";
        }
        finally
        {
            Ocupada = false;
            PassoMudou?.Invoke("", null);
        }

        Respondeu?.Invoke(resposta);
    }

    /// <summary>Roda um turno com o que o usuário escreveu na barra.</summary>
    public async Task EnviarAsync(string texto)
    {
        if (string.IsNullOrWhiteSpace(texto) || Ocupada) return;

        if (ComandoDe(texto) is var comando and not Comando.Nenhum)
        {
            await RodarComandoAsync(comando);
            return;
        }

        Ocupada = true;
        _parou = false;
        Marcar();
        string fala = "";
        string? erro = null;

        try
        {
            PassoMudou?.Invoke("Pensando", null);

            await foreach (var item in Conversa.StreamResponseAsync(texto, Console.Write))
            {
                switch (item)
                {
                    case ChatStreamItem.ToolStarted iniciada:
                        PassoMudou?.Invoke(Ferramentas.Rotulo(iniciada.Tool), iniciada.Tool);
                        break;
                    case ChatStreamItem.ToolFinished terminada:
                        AIB.Views.ChatWindow.RegistrarAcao(terminada);
                        PassoMudou?.Invoke("Pensando", null);
                        break;
                    case ChatStreamItem.SegmentBreak:
                        fala += "\n\n";
                        break;
                    case ChatStreamItem.Text t:
                        fala += t.Value;
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (_parou)
        {
            // Parar não é erro: a barra não mostra "algo deu errado" para o que o usuário pediu.
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ORBE] turno falhou: {ex.Message}");
            erro = $"Algo deu errado: {ex.Message}";
        }
        finally
        {
            Ocupada = false;
            Marcar();
            PassoMudou?.Invoke("", null);
        }

        UsuarioFalou?.Invoke(texto);

        string final = erro ?? QuebraDeFala.Limpar(fala).Trim();
        Respondeu?.Invoke(final.Length > 0 ? final : _parou ? "Parei." : "Feito.");
    }

    /// <summary>
    /// Uma fala que ela puxa sozinha (lembrete, iniciativa): entra no histórico vivo, para a
    /// resposta do usuário continuar o assunto.
    /// </summary>
    public void ReceberFalaPropria(string texto)
    {
        if (string.IsNullOrWhiteSpace(texto)) return;
        Conversa.AppendAssistantText(texto.Trim());
        Marcar();
    }

    /// <summary>
    /// Parada há mais que <see cref="Pausa"/> e com algo novo desde a última vez: compacta. Uma
    /// vez por pausa — sem a marca, cada batida pediria de novo e ouviria "nada a compactar".
    /// </summary>
    public async Task CompactarSeParadaAsync(DateTime agoraUtc)
    {
        if (!DeveCompactar(_compactarNaPausa, Ocupada, UltimaAtividadeUtc, agoraUtc)) return;

        _compactarNaPausa = false;
        try
        {
            string resultado = await Conversa.ForcarCompactacaoAsync(Nivel).ConfigureAwait(true);
            Console.WriteLine($"[ORBE] pausa de {Pausa.TotalHours:0} h: {resultado}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ORBE] compactação da pausa falhou: {ex.Message}");
        }
    }

    /// <summary>Pura, para o ensaio.</summary>
    public static bool DeveCompactar(bool algoNovo, bool ocupada, DateTime? ultimaAtividadeUtc, DateTime agoraUtc) =>
        algoNovo && !ocupada && ultimaAtividadeUtc is DateTime u && agoraUtc - u >= Pausa;

    /// <summary>Palavras de um texto, para medir se a resposta foi conversa ou só "ok".</summary>
    public static int Palavras(string? texto) =>
        (texto ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

    private void Marcar()
    {
        UltimaAtividadeUtc = DateTime.UtcNow;
        _compactarNaPausa = true;
    }
}
