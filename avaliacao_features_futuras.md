# Molecular — Features futuras que valem a pena

**Escopo:** avaliação de produto (não implementação).  
**Premissa:** o [ROADMAP.md](ROADMAP.md) P0/P1 está **confirmado** e deve ser feito antes. Este documento cobre o que seria **excelente acrescentar depois** (ou no fim de P1), sem atrasar a validação diária do mixer.

**Critério de “excelente”:** aumenta retenção no uso diário, reforça o diferencial do Molecular (mixer pessoal + mídia + segurança sonora), e cabe no Windows/Core Audio/GSMTC sem virar Voicemeeter/DAW.

Relacionado: [avaliacao_perf_bugs_mvp](avaliacao_perf_bugs_mvp_85010f8a.plan.md) · [avaliacao_seguranca_mvp](avaliacao_seguranca_mvp_ba87c04a.plan.md) · evidências em [reports/](reports/).

---

## O que NÃO repetir aqui

Já planejado e confirmado — só executar quando chegar a hora:

| Fase | Itens |
|------|--------|
| P0 | Autostart, recuperação de áudio, backup de perfil, logs, gate de estabilidade |
| P1 | Perfis múltiplos, reordenar/fixar, settings, a11y/atalhos locais |
| P2 roadmap | Hotkeys globais, roteamento avançado, EQ/DSP, auto-update, sync, telemetria |
| Bugs/UX já avaliados | Unmute no exit, throttle na bandeja, thumbnail GSMTC, modal Adicionar canal, limpar solos |

Este arquivo foca em **features de valor** além (ou refinando) essa base.

---

## Tier S — Diferencial forte (fazer cedo após P0 estável)

### 1. Canal favorito / pin expandido (com controles de mídia)

**Por quê:** o usuário pediu explicitamente (ex.: Spotify sempre expandido com pause / pular / voltar). É o “dock” de transporte do dia a dia.

**Como encaixa:** `ChannelBinding.IsPinned` já existe e não é usado. Combinar com `ViewMode=expanded`, respeitar no “Colapsar todos”, restaurar ao abrir.

**Por que é excelente:** transforma o Molecular de “só volume” em **controle de mídia persistente** sem abrir o app de origem.

### 2. Cenas / presets de volume com um clique

**Por quê:** “Trabalho”, “Jogo”, “Filme”, “Call” — muda vários faders de uma vez (Discord baixo, jogo alto, browser mudo, etc.).

**Como encaixa:** depois de perfis P1, ou como *snapshots* leves dentro do perfil Principal (menos ambicioso que multi-perfil completo).

**Por que é excelente:** valor imediato em 1 segundo; poucas apps de mixer fazem isso bem na UI.

### 3. Ducking inteligente (voz / call prioriza)

**Por quê:** quando Discord/Zoom/Teams detecta atividade (peak), baixa automaticamente Spotify/jogo e sobe de volta.

**Como encaixa:** regras por canal (“este é comunicação”) + teto/safety já existentes; sem DSP complexo.

**Por que é excelente:** sensação “mágica” no uso real; reforça a identidade de *mixer pessoal*, não só faders manuais.

### 4. Atalhos globais (já P2, mas priorizar cedo)

**Por quê:** mute global, solo limpo, play/pause do canal pinado, abrir/esconder janela — sem tirar o foco do jogo/IDE.

**Cuidado:** registrar hotkeys com cuidado (conflitos, opção off por padrão) — alinhado à avaliação de segurança (sem injeção; APIs de hotkey do Windows).

**Por que é excelente:** quem usa mixer todo dia quase sempre pede isso.

---

## Tier A — Excelente para retenção e polish

### 5. Limpar / alternar isolamento (solo) em massa

Já na avaliação de bugs/UX. Botão global que limpa todos os solos e, conforme perfil, restaura o conjunto salvo. Complementa o pin/favorito.

### 6. Grupos de canais (bus leve)

Ex.: agrupar “Browsers”, “Chat”, “Jogos” com fader-mãe que escala os filhos. Menos poderoso que roteamento P2, muito mais útil no dia a dia.

### 7. Regras automáticas por app

Ao detectar `game.exe` / `spotify.exe` / `discord.exe`: aplicar volume inicial, pin, cor, perfil/cena. Reduz setup repetitivo após reboot.

### 8. Mini-modo / compact overlay

Janela pequena sempre no topo (opcional): 1–3 canais pinados + mute global. Não é overlay no processo do jogo — janela própria do Molecular (seguro para anticheat).

### 9. Histórico leve de “o que tocou alto”

Não EQ: só um gráfico/lista “últimos 15 min de pico por app” para achar quem estourou o volume. Cabe no P2 “análise histórica”, versão mínima.

### 10. Perfis por foco de janela (opcional)

Quando o jogo em fullscreen ganha foco → cena “Jogo”; ao voltar ao desktop → cena “Desktop”. Opt-in; pode ser frágil com exclusivos — começar com apps em janela.

---

## Tier B — Bom, mas depois do core

### 11. Equalizador / DSP por canal

Já no ROADMAP P2. Excelente para power users, **caro** (latência, CPU, percepção de “driver de áudio”). Só após o mixer ser impecável.

### 12. Roteamento multi-device (Voicemeeter-lite)

Ex.: browser no headset, jogo no DAC. Valor alto, complexidade e risco de suporte altos. Manter P2 tardio.

### 13. Widgets / integração Stream Deck / OSC

Nicho streamer/produtividade. Ótimo marketing, não essencial ao MVP pessoal.

### 14. Sincronização de perfis / nuvem

Útil com vários PCs; exige conta, privacidade e custo. Depois de export/import local (P1).

### 15. Assinatura Authenticode / Store

Distribuição, não feature de produto. Pago (certificado). Adiar até distribuir para terceiros.

### 16. Temas / skins

Legal, pouco diferencial se o produto ainda oscila em estabilidade.

---

## Tier C — Evitar ou reformular

| Ideia | Por quê evitar (ou reformular) |
|-------|--------------------------------|
| Injeção / hook em jogos | Ban/anticheat — fora do escopo Molecular |
| Captura de tela / overlay no processo do jogo | Mesmo risco; preferir janela própria |
| “AI mix” genérico sem regra clara | Marketing vazio; ducking por peak é melhor |
| Clonar Voicemeeter inteiro | Dilui o foco; suporte explode |
| Telemetria default-on | Conflita com postura de privacidade do README |

---

## Ordem sugerida (pós-P0)

```text
P0 completo (roadmap confirmado)
  → P1 mínimo: settings + pin/favorito + limpar solos + perfis básicos
  → Tier S: cenas/presets → ducking → hotkeys globais
  → Tier A: grupos, regras por app, mini-modo
  → Tier B: DSP / roteamento / Store conforme demanda real
```

### Matriz rápida valor × esforço

| Feature | Valor | Esforço | Momento |
|---------|-------|---------|---------|
| Pin/favorito expandido | Alto | Baixo (`IsPinned` já existe) | Fim P0 / início P1 |
| Limpar solos em massa | Alto | Baixo | Início P1 |
| Cenas / presets | Muito alto | Médio | P1 |
| Ducking por peak | Muito alto | Médio | P1 tardio |
| Hotkeys globais | Alto | Médio | P2 antecipado |
| Grupos / bus | Alto | Médio–alto | P1/P2 |
| Regras por app | Alto | Médio | P1 |
| Mini-modo | Médio–alto | Médio | P1 |
| EQ/DSP | Alto (nicho) | Alto | P2 |
| Multi-device routing | Alto (nicho) | Muito alto | P2+ |
| Authenticode | Distribuição | $ + processo | Quando for publicar |

---

## Norte de produto

O Molecular fica excelente se for lembrado como:

> “O mixer que deixa meu áudio previsível, com Spotify/Discord à mão, sem eu brigar com o Windows.”

Tudo que reforça **previsibilidade**, **atalhos de cena** e **mídia pinada** entra no Tier S/A. Tudo que vira studio/virtual cable fica no B, depois de provar o valor principal.
