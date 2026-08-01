# Molecular — Roadmap do MVP

## Objetivo do MVP

Validar que o Molecular pode ser usado diariamente como mixer pessoal de áudio do Windows, mantendo controle previsível das sessões, baixo consumo de recursos e recuperação automática após mudanças do sistema.

O MVP estará validado quando deixar de ser apenas uma demonstração visual e puder permanecer ativo durante uma jornada completa sem exigir correções manuais do usuário.

## Estado atual

### Concluído

- Detecção e controle de sessões pelo Core Audio.
- Lista persistente de sessões com eventos de criação, volume, mute, estado e desconexão.
- Atualização periódica apenas do pico dos medidores.
- Controle de volume horizontal e vertical sincronizado.
- Mute, solo e mute global.
- Teto global de segurança aplicado ao valor visual e efetivo.
- Adição, expansão, ocultação e restauração de canais.
- Persistência do perfil principal.
- Controles básicos de mídia quando fornecidos pelo Windows.
- Interface responsiva para janela e tela cheia.
- Registro de falhas fatais de inicialização e interface.
- Remoção de canal com ação explícita e opção de desfazer por 8 segundos.
- Instância única por usuário com restauração da janela já aberta.
- Operação em segundo plano pela bandeja, com abrir, silenciar, restaurar e sair.
- Identidade visual do aplicativo aplicada à janela, executável e bandeja.
- Opção `Iniciar com o Windows` nas configurações.
- Backup automático do perfil e recuperação a partir do `.bak` com aviso ao usuário.
- Restauração dos mutes do Windows ao sair (mute global / solo não ficam grudados no SO).
- Reconexão após retomada de energia e falha de áudio, com estados `Reconectando` / `Dispositivo indisponível` / `Sistema de áudio ativo`.
- Log operacional circular e `Exportar diagnóstico` nas configurações (sem títulos de mídia).
- Polling reduzido na bandeja (1 Hz) e GSMTC pausado em segundo plano.
- **Gate P0.6 fechado** (testes + evidências de estabilidade / DPI).
- Perfis múltiplos (criar, duplicar, excluir, renomear, padrão) e atrelamento a aplicativo aberto.

### Lacunas que impedem validação oficial

- Validação diária prolongada (7+ dias) e soak 8h contínuo na bandeja ficam como acompanhamento operacional.
- Itens restantes de P1 (a11y completa) ainda abertos.

---

## P0 — Prioridade máxima para o MVP

Estes itens devem ser concluídos antes de iniciar uma validação diária oficial.

### P0.1 — Ciclo completo de canais

**Entregas**

- Adicionar a ação `Remover canal` no card expandido.
- Remover a atribuição e o canal do perfil, sem deixar posições vazias.
- Exibir notificação com `Desfazer` por alguns segundos.
- Impedir remoção acidental por clique único em um ícone ambíguo.
- Atualizar imediatamente contadores, paginação, canais ocultos e canais expandidos.

**Critérios de aceite**

- O canal desaparece das coleções rápida e expandida.
- Reiniciar o Molecular não restaura um canal removido.
- `Desfazer` restaura posição, volume, mute, solo e estado oculto.
- Remover um canal não altera o volume real do aplicativo.

### P0.2 — Instância única e operação em segundo plano

**Entregas**

- Garantir apenas uma instância do Molecular por usuário.
- Ao abrir novamente, trazer a janela existente para frente.
- Adicionar ícone na bandeja do Windows.
- Fechar a janela deve minimizar para a bandeja quando essa opção estiver ativa.
- Menu da bandeja com `Abrir`, `Silenciar tudo`, `Restaurar áudio` e `Sair`.
- Configuração opcional `Iniciar com o Windows`.

**Critérios de aceite**

- Nunca existem dois processos controlando as mesmas sessões.
- O mixer continua funcionando com a janela fechada na bandeja.
- `Sair` encerra o processo e libera todos os callbacks do Core Audio.
- A inicialização automática pode ser ativada e desativada pelo usuário.

### P0.3 — Recuperação do sistema de áudio

**Entregas**

- Detectar suspensão e retomada do Windows.
- Reconstruir o monitor após reinício ou indisponibilidade do serviço de áudio.
- Recuperar automaticamente após remoção do dispositivo ativo.
- Selecionar o dispositivo padrão como fallback quando o preferido desaparecer.
- Mostrar estado visível: `Reconectando`, `Dispositivo indisponível` ou `Sistema de áudio ativo`.
- Aplicar tentativas com intervalo progressivo, sem loop agressivo.

**Critérios de aceite**

- Suspender e retomar o computador não exige reiniciar o Molecular.
- Desconectar um dispositivo USB ou Bluetooth não trava a interface.
- O fallback não altera volumes de aplicativos indevidos.
- O serviço volta ao estado ativo automaticamente quando o dispositivo retorna.

### P0.4 — Persistência resistente a falhas

**Entregas**

- Manter gravação atômica do perfil.
- Criar backup da última versão válida antes de substituir o arquivo.
- Validar o JSON e os limites antes de aceitar o perfil carregado.
- Recuperar automaticamente pelo backup quando o arquivo principal estiver corrompido.
- Informar ao usuário quando uma recuperação ocorrer.

**Critérios de aceite**

- Interromper uma gravação não destrói o último perfil válido.
- Perfil corrompido é isolado para diagnóstico.
- O backup preserva canais, ordem, volumes, mute, solo e ocultação.

### P0.5 — Diagnóstico operacional

**Entregas**

- Log circular com tamanho máximo e retenção definida.
- Registrar inicialização, dispositivo ativo, criação e remoção de sessões, recuperação e erros.
- Não registrar título de mídia, nome de música ou informações pessoais por padrão.
- Adicionar `Exportar diagnóstico` nas configurações.
- Incluir versão, Windows, escala de tela, dispositivo, contagens e erros recentes.

**Critérios de aceite**

- Um erro de áudio pode ser investigado sem depender apenas de captura de tela.
- O log não cresce indefinidamente.
- A exportação não contém dados de mídia sem consentimento explícito.

### P0.6 — Gate de estabilidade

**Status: GATE TÉCNICO CONCLUÍDO (31/07/2026); validação prolongada em andamento.**
Gate técnico fechado com automação (25 testes em 01/08/2026), throttle na bandeja, evidências manuais de memória/DPI e critérios de código para mute-ao-sair, rebuild e recuperação de perfil. O uso prolongado de 7 dias continua sendo o gate da beta.

**Entregas**

- [x] Testes automatizados do ciclo adicionar, ocultar, restaurar, remover e desfazer.
- [x] Testes de perfil corrompido e recuperação pelo backup.
- [x] Testes de instância única.
- [x] Teste de rebuild do monitor sob demanda (proxy de troca de dispositivo).
- [x] Polling em segundo plano reduzido (1 Hz na bandeja; GSMTC pausado).
- [x] Checklist manual / evidências abaixo.

**Critérios para liberar a validação interna**

- [x] Memória sem crescimento contínuo na amostra curta de 31/07/2026: ~60,9 MB (10:38) → ~63–64 MB (10:50) → pico ~67 MB (10:55) → ~59 MB (11:01). O soak de 8h e o uso por 7 dias permanecem como validação operacional.
- [x] CPU baixa em segundo plano — throttle 1 Hz + GSMTC pausado na bandeja (código).
- [x] Nenhuma duplicação de sessão ou canal — testes de perfil/canais.
- [x] Volume zero estável — loop trata fader ~0 como mute intencional (sem toggle repetido).
- [x] Troca de dispositivo — rebuild sob demanda testado; PowerMode/device notifications no código.
- [x] Perfil restaurado após falha — backup `.bak` + quarentena + testes.
- [x] Interface em 100% / 125% / 150% — validado manualmente (ok).

#### Checklist P0.6 (fechamento)

- [x] Uso ~10+ min com janela aberta: memória estável (notas 31/07/2026 acima).
- [x] Bandeja: poll reduzido a 1 Hz / GSMTC off (implementado).
- [x] Silenciar tudo → Sair: restauração de mute no SO no `Dispose` (implementado).
- [x] Suspender/retomar: `PowerModeChanged` → Reconectando / estados de saúde (implementado).
- [x] Rebuild após pedido de troca de device (teste automatizado).
- [x] Perfil corrompido → `.bak` ou reset com aviso (teste automatizado).
- [x] Escala 100% / 125% / 150%: ok (manual).
- [x] Preferência de dispositivo preservada durante fallback e fallback desligado respeitado (testes automatizados).
- [x] Remoção persistente com busca automática e rearmamento de perfil atrelado (testes automatizados).
- [ ] (Opcional / contínuo) Soak 8h na bandeja — não bloqueia P0; acompanhar na validação diária.

---

## P1 — Beta controlada

Executar após todos os itens P0 passarem pelo gate de estabilidade.

### Perfis completos

- [x] Criar, renomear, duplicar, selecionar e excluir perfis.
- [x] Definir um perfil padrão.
- [x] Atrelar perfil a um aplicativo aberto (ativa ao detectar o app; ao fechar o app ou sair do perfil atrelado, restaura o padrão).
- [x] Importar e exportar perfis sem incluir dados sensíveis.
- [x] Busca automática de canais por perfil (opt-in; pode desligar a qualquer momento).

### Organização de canais

- [x] Reordenar canais.
- [x] Fixar canais prioritários.
- [x] Definir cores sem bordas serrilhadas (faixa de acento + paleta).
- [x] Pesquisa quando houver muitos canais.

### Configurações persistentes

- [x] Comportamento do botão fechar (bandeja ou sair).
- [x] Inicialização com o Windows.
- [x] Dispositivo preferido e fallback para o padrão do Windows.
- [x] Frequência visual dos medidores.
- [x] Abrir na bandeja.

### Acessibilidade e teclado

- Navegação completa por teclado.
- Foco visível consistente.
- Nomes acessíveis para todos os controles.
- Atalhos locais configuráveis.

### Gate da beta

- Uso diário em Windows 10 e Windows 11.
- Validação com saídas P2, USB, HDMI e Bluetooth.
- Nenhuma perda de perfil em sete dias de uso.
- Diagnósticos suficientes para reproduzir os problemas encontrados.

---

## P2 — Após validação do valor principal

- Atalhos globais.
- Roteamento avançado.
- Análise histórica de áudio.
- Equalizador, DSP e efeitos.
- Atualização automática.
- Sincronização de perfis.
- Telemetria opcional e anônima.

Esses recursos não devem atrasar a validação do mixer principal.

---

## Ordem de execução recomendada

1. Remover canal com desfazer.
2. Instância única.
3. Bandeja e encerramento correto.
4. Iniciar com o Windows.
5. Recuperação de suspensão e dispositivos.
6. Backup e recuperação do perfil.
7. Diagnóstico operacional e exportação.
8. Gate de estabilidade P0.
9. Perfis completos e organização dos canais.
10. Beta controlada.

## Próxima entrega recomendada

Continuar **P1**: acessibilidade e teclado (navegação, foco, atalhos locais).

Branch: `roadmap/p0-p3`.
