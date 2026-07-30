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

### Lacunas que impedem validação oficial

- Não existe opção para iniciar com o Windows.
- A recuperação após suspensão, reinício do serviço de áudio ou troca brusca de dispositivo ainda não possui fluxo visível e testado.
- O perfil não possui backup e recuperação automática contra corrupção.
- Os logs atuais cobrem falhas fatais, mas não o histórico operacional do Core Audio.
- Só existe o perfil `Principal`; criação e troca de perfis ainda não foram implementadas.

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

**Entregas**

- Testes automatizados do ciclo adicionar, ocultar, restaurar, remover e desfazer.
- Testes de perfil corrompido e recuperação pelo backup.
- Testes de instância única.
- Testes de troca e remoção de dispositivo.
- Checklist manual de redimensionamento e escala do Windows.

**Critérios para liberar a validação interna**

- 8 horas de execução sem crescimento contínuo de memória.
- CPU baixa com medidores sem atividade.
- Nenhuma duplicação de sessão ou canal.
- Volume zero estável, sem alternância de mute.
- Troca de dispositivo recuperada sem reiniciar o aplicativo.
- Perfil restaurado corretamente após encerramento inesperado.
- Interface validada em 100%, 125% e 150% de escala.

---

## P1 — Beta controlada

Executar após todos os itens P0 passarem pelo gate de estabilidade.

### Perfis completos

- Criar, renomear, duplicar, selecionar e excluir perfis.
- Definir um perfil padrão.
- Importar e exportar perfis sem incluir dados sensíveis.

### Organização de canais

- Reordenar canais.
- Fixar canais prioritários.
- Definir cores sem bordas serrilhadas.
- Pesquisa quando houver muitos canais.

### Configurações persistentes

- Comportamento do botão fechar.
- Inicialização com o Windows.
- Dispositivo preferido e fallback.
- Frequência visual dos medidores.
- Abrir minimizado.

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

Começar por **P0.1 — Ciclo completo de canais** e **P0.2 — Instância única**.

Esses dois itens fecham os maiores riscos imediatos: o usuário precisa controlar o ciclo de vida dos canais, e o sistema não pode permitir dois mixers concorrentes alterando as mesmas sessões do Windows.
