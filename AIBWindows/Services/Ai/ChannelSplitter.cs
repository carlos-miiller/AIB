using System;
using System.Collections.Generic;
using System.Text;

namespace AIB.Services.Ai;

/// <summary>
/// Separa o texto cru do modelo em canal Final e canal Reasoning, lidando com
/// &lt;think&gt;…&lt;/think&gt; e com os markers de canal estilo Harmony (gemma4).
/// Mantém um carry buffer para markers partidos entre chunks.
/// Uma instância por stream — tem estado.
///
/// Modos:
///   0 = Streaming    -> texto vai para o canal Final (após strip de tokens de template)
///   1 = InsideThink  -> dentro de &lt;think&gt;…&lt;/think&gt;, vai para o canal Reasoning
///   2 = WaitingFinal -> após &lt;/think&gt;, bufferiza até ver &lt;channel|&gt;/&lt;|message|&gt;.
///                       Esse intervalo é o canal "analysis/commentary" do gemma4 —
///                       parece pensamento estruturado mas NÃO é a resposta.
/// </summary>
public sealed class ChannelSplitter
{
    private const string ThinkOpen = "<think>";
    private const string ThinkClose = "</think>";

    private static readonly string[] FinalChannelMarkers = { "<channel|>", "<|channel|>", "<|message|>" };
    private static readonly string[] MarkersToWatch = { "<think>", "</think>", "<channel|>", "<|channel|>", "<|message|>" };

    private readonly StringBuilder _waitBuffer = new();
    private readonly StringBuilder _thinkBuffer = new();
    private readonly StringBuilder _finalText = new();
    private readonly StringBuilder _rawText = new();

    private string _carry = "";
    private int _mode;
    private bool _anyFinal;

    /// <summary>
    /// Se este stream trouxe raciocínio pelo CAMPO separado do Ollama (message.thinking) em vez
    /// de tags &lt;think&gt; embutidas no content. Modelos de raciocínio modernos usam o campo;
    /// a máquina de markers nunca vê nada e o raciocínio se perderia calado.
    /// </summary>
    private bool _separateThinking;

    /// <summary>Diagnóstico para o log [STREAM-END].</summary>
    public int Mode => _mode;

    /// <summary>Diagnóstico para o log [STREAM-END].</summary>
    public bool AnythingEmittedAsFinal => _anyFinal;

    /// <summary>Todo o texto classificado como Final neste stream. Entrada do healer.</summary>
    public string FinalText => _finalText.ToString();

    /// <summary>
    /// Texto CRU do stream, exatamente como o modelo emitiu — com os blocos
    /// &lt;think&gt;…&lt;/think&gt; e os markers de canal. É isto que vai para o HISTÓRICO:
    /// o modelo enxerga o próprio raciocínio nas iterações seguintes (continuidade de
    /// plano em tarefas multi-passo). O usuário continua vendo só o canal final.
    /// </summary>
    public string RawText => _rawText.ToString();

    /// <summary>
    /// Consome raciocínio vindo do campo separado <c>message.thinking</c> do Ollama.
    /// <para>
    /// Não passa pela máquina de markers: aqui não há nada a detectar, o provider já
    /// classificou. No <see cref="RawText"/> o texto entra envolto em &lt;think&gt;…&lt;/think&gt;
    /// para que o histórico continue coerente — é assim que o modelo relê o próprio raciocínio
    /// nas iterações seguintes, exatamente como acontece com os modelos que usam tags inline.
    /// </para>
    /// </summary>
    public IReadOnlyList<StreamChunk.TextDelta> PushThinking(string chunk)
    {
        var output = new List<StreamChunk.TextDelta>();
        if (string.IsNullOrEmpty(chunk)) return output;

        if (!_separateThinking)
        {
            _separateThinking = true;
            _rawText.Append(ThinkOpen);
        }

        _rawText.Append(chunk);
        _thinkBuffer.Append(chunk);
        output.Add(new StreamChunk.TextDelta(chunk, TextChannel.Reasoning));
        return output;
    }

    /// <summary>
    /// Fecha o bloco &lt;think&gt; aberto por <see cref="PushThinking"/>, se houver. Idempotente.
    /// </summary>
    private void CloseSeparateThinking()
    {
        if (!_separateThinking) return;
        _separateThinking = false;
        _rawText.Append(ThinkClose);
    }

    /// <summary>Consome um pedaço cru e devolve os deltas já classificados, na ordem.</summary>
    public IReadOnlyList<StreamChunk.TextDelta> Push(string rawChunk)
    {
        var output = new List<StreamChunk.TextDelta>();
        if (string.IsNullOrEmpty(rawChunk)) return output;

        // A chegada de content marca o FIM do raciocínio: fecha o bloco antes de escrever a
        // resposta no texto cru. Fechar só no Flush colocava o </think> depois de tudo, e a
        // resposta final acabava dentro do bloco de raciocínio no histórico — o modelo relia
        // o próprio turno e não encontrava resposta alguma.
        CloseSeparateThinking();

        _rawText.Append(rawChunk);

        // Carry buffer dinâmico: chunks pequenos podem partir markers ("<th" + "ink>x").
        // Retém no carry só o sufixo que ainda pode ser início de um marker vigiado.
        string combined = _carry + rawChunk;
        int retain = FindTrailingMarkerPrefix(combined, MarkersToWatch);
        string remaining = combined.Substring(0, combined.Length - retain);
        _carry = retain > 0 ? combined.Substring(combined.Length - retain) : "";

        Consume(remaining, output);
        return output;
    }

    /// <summary>
    /// Fecha o stream: processa o carry retido e aplica o flush em camadas
    /// (WaitingFinal → o buffer É a resposta; InsideThink não fechado → vaza o think
    /// como resposta se nada foi emitido e não houve tool call).
    /// </summary>
    public IReadOnlyList<StreamChunk.TextDelta> Flush(bool anyToolCallSeen)
    {
        var output = new List<StreamChunk.TextDelta>();

        // Turno que só raciocinou e não emitiu content: o bloco ainda está aberto aqui.
        CloseSeparateThinking();

        if (_carry.Length > 0)
        {
            string remaining = _carry;
            _carry = "";
            Consume(remaining, output);
        }

        // 1. Terminou em WaitingFinal (modelo usou <think> mas nunca emitiu <channel|>):
        //    o buffer É a resposta real. Acontece com qwen com thinking, modelos sem
        //    harmony, ou gemma4 quando responde sem channels.
        if (_mode == 2 && _waitBuffer.Length > 0)
        {
            EmitFinal(_waitBuffer.ToString(), output);
            _waitBuffer.Clear();
        }
        // 2. O turno RACIOCINOU e não respondeu: nada no canal final, nenhuma ferramenta, e
        //    raciocínio acumulado. O conteúdo do pensamento era, de fato, a resposta. Vaza
        //    para o usuário como último recurso — melhor que a bolha vazia.
        //
        //    Cobre os dois caminhos pelos quais o raciocínio chega:
        //
        //    - tag inline aberta e nunca fechada (_mode == 1), bug do modelo;
        //    - campo separado message.thinking (_mode == 0), que é como qwen3.5 e os demais
        //      modelos de raciocínio modernos operam.
        //
        //    O segundo caso não era coberto: a condição exigia _mode == 1, e o campo separado
        //    nunca muda o modo. Um turno que só pensou terminava com rawText=0ch e o usuário
        //    via um balão vazio, com a resposta certa presa no raciocínio — foi exatamente o
        //    que aconteceu numa busca por arquivo: o caminho encontrado ficou só no
        //    pensamento.
        else if (_thinkBuffer.Length > 0 && !_anyFinal && !anyToolCallSeen)
        {
            string pensamento = _thinkBuffer.ToString();

            // Nem todo pensamento é resposta. Visto em produção: o modelo escreveu a chamada de
            // ferramenta COMO TEXTO, em XML de outro dialeto, dentro do canal de raciocínio — e
            // o fallback despejou isso na tela do usuário como se fosse a resposta:
            //
            //     <parameter=command>
            //     powershell -ExecutionPolicy Bypass -File "...\gerar_sigs.ps1"
            //     </parameter>
            //     </function>
            //     </tool_call>
            //
            // Nada foi executado, e o que apareceu não era português. Melhor uma frase honesta
            // que o esqueleto de uma chamada que não aconteceu.
            if (ChamadaMalformada(pensamento))
            {
                Console.WriteLine(
                    "[STREAM-END] Fallback DESCARTADO: o pensamento era uma chamada de ferramenta "
                    + "malformada, escrita como texto. Nada foi executado.");

                EmitFinal(RecadoDeChamadaMalformada, output);
            }
            else
            {
                int before = output.Count;
                EmitFinal(pensamento, output);
                if (output.Count > before)
                {
                    Console.WriteLine(
                        "[STREAM-END] Fallback: o turno só raciocinou; emitindo o pensamento como resposta.");
                }
            }
        }

        _thinkBuffer.Clear();
        return output;
    }

    /// <summary>O que o usuário lê quando o modelo errou a sintaxe da ferramenta.</summary>
    public const string RecadoDeChamadaMalformada =
        "Tentei usar uma ferramenta, mas escrevi a chamada como texto em vez de executá-la. "
        + "Nada foi executado. Pode pedir de novo?";

    /// <summary>
    /// Se o texto é o esqueleto de uma chamada de ferramenta em vez de prosa.
    /// <para>
    /// Procura as marcas dos dialetos que os modelos abertos emitem quando erram o formato —
    /// Hermes e ChatML de função. São sequências que não aparecem numa frase escrita para gente.
    /// </para>
    /// </summary>
    public static bool ChamadaMalformada(string? texto)
    {
        string t = texto ?? "";
        if (t.Length == 0) return false;

        foreach (string marca in new[]
                 {
                     "<tool_call", "</tool_call", "<function=", "</function", "<parameter=",
                     "</parameter", "<|tool_call", "<invoke name="
                 })
        {
            if (t.Contains(marca, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    private void Consume(string remaining, List<StreamChunk.TextDelta> output)
    {
        while (remaining.Length > 0)
        {
            if (_mode == 1) // InsideThink
            {
                int closeIdx = remaining.IndexOf(ThinkClose, StringComparison.OrdinalIgnoreCase);
                if (closeIdx < 0)
                {
                    EmitReasoning(remaining, output);
                    _thinkBuffer.Append(remaining); // guarda para fallback caso </think> nunca chegue
                    remaining = "";
                }
                else
                {
                    EmitReasoning(remaining.Substring(0, closeIdx + ThinkClose.Length), output);
                    _thinkBuffer.Clear(); // think fechou normalmente, descarta fallback
                    remaining = remaining.Substring(closeIdx + ThinkClose.Length);
                    _mode = 0; // sai de InsideThink e volta direto para Streaming normal
                }
            }
            else if (_mode == 2) // WaitingFinal — bufferiza até ver marker de canal final
            {
                int markerIdx = -1;
                int markerLen = 0;
                foreach (var m in FinalChannelMarkers)
                {
                    int idx = remaining.IndexOf(m, StringComparison.OrdinalIgnoreCase);
                    if (idx >= 0 && (markerIdx < 0 || idx < markerIdx))
                    {
                        markerIdx = idx;
                        markerLen = m.Length;
                    }
                }

                if (markerIdx < 0)
                {
                    // Sem marker neste chunk — bufferiza, mostra no console
                    _waitBuffer.Append(remaining);
                    EmitReasoning(remaining, output);
                    remaining = "";
                }
                else
                {
                    // Marker encontrado: descarta buffer e tudo antes (pre-final)
                    if (markerIdx > 0)
                        EmitReasoning(remaining.Substring(0, markerIdx), output);
                    _waitBuffer.Clear();
                    remaining = remaining.Substring(markerIdx + markerLen);
                    _mode = 0; // volta para Streaming — agora é canal final
                }
            }
            else // _mode == 0, Streaming
            {
                int thinkIdx = remaining.IndexOf(ThinkOpen, StringComparison.OrdinalIgnoreCase);
                if (thinkIdx < 0)
                {
                    EmitFinal(remaining, output);
                    remaining = "";
                }
                else
                {
                    if (thinkIdx > 0)
                        EmitFinal(remaining.Substring(0, thinkIdx), output);

                    EmitReasoning(ThinkOpen, output);
                    remaining = remaining.Substring(thinkIdx + ThinkOpen.Length);
                    _mode = 1; // entra em InsideThink
                }
            }
        }
    }

    private void EmitFinal(string text, List<StreamChunk.TextDelta> output)
    {
        string stripped = ChatTemplateSanitizer.Strip(text);
        if (string.IsNullOrEmpty(stripped)) return;

        _anyFinal = true;
        _finalText.Append(stripped);
        output.Add(new StreamChunk.TextDelta(stripped, TextChannel.Final));
    }

    private static void EmitReasoning(string text, List<StreamChunk.TextDelta> output)
    {
        if (string.IsNullOrEmpty(text)) return;
        output.Add(new StreamChunk.TextDelta(text, TextChannel.Reasoning));
    }

    /// <summary>
    /// Retorna o tamanho do maior sufixo de <paramref name="text"/> que é prefixo
    /// (de pelo menos 1 char) de algum dos markers em <paramref name="markers"/>.
    /// Mantém o carry buffer só pelo tempo necessário: enquanto o fim do texto acumulado
    /// pode ser início de um marker, espera. Quando não pode, processa.
    /// </summary>
    private static int FindTrailingMarkerPrefix(string text, string[] markers)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        int maxOverlap = 0;
        foreach (var marker in markers)
        {
            int maxK = Math.Min(marker.Length - 1, text.Length);
            for (int k = maxK; k > maxOverlap; k--)
            {
                if (string.Compare(text, text.Length - k, marker, 0, k, StringComparison.OrdinalIgnoreCase) == 0)
                {
                    maxOverlap = k;
                    break;
                }
            }
        }
        return maxOverlap;
    }
}
