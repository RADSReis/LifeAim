# Plano — melhorar a experiência do Pippin

Escrito em 16/11/2026, depois da primeira versão do diário de emagrecimento (módulo `Fitness` + `FitnessAgent`).
Cada fase é independente e pode ser executada por outro modelo, na ordem abaixo, uma por vez.

## Como ler este plano

- **Evidência** diz de onde veio o problema: leitura do código ou o teste com a IA real (`FitnessRealAiTests`, 8 mensagens em 3 minutos).
- **Hipótese** marca o que ainda precisa ser confirmado antes de mexer.
- **Pronto quando** é o critério de aceite. Sem ele cumprido, a fase não está terminada.

## Regras para quem executar

1. Ler `CLAUDE.md` e as páginas do Notion "01 — Funcionalidades", "02 — Experiência do Usuário" e "04 — Arquitetura" antes de começar.
2. Tudo em .NET. Monólito modular: um módulo só fala com outro pela pasta `Contracts`. Um schema PostgreSQL por módulo, ADO.NET com Npgsql, sem Entity Framework. Migrations só para frente, em `Infrastructure/Migrations/NNNN_nome.sql`.
3. Código e comentários em inglês; todo texto que o usuário lê, em português. Respostas ao autor em português.
4. Nunca colocar IPs, usuários, nomes de chave ou segredos em arquivos, commits, GitHub ou Notion. Os registros pessoais do autor (peso, refeições) também nunca entram no repositório.
5. A IA escolhe e escreve; o código confere, soma e decide. Nenhum total, data ou saldo é calculado pelo modelo.
6. Cada fase termina com `dotnet build LifeAim.sln` sem avisos e `dotnet test LifeAim.sln --filter "Category!=RealAI"` todo verde.
7. Testes com a IA real gastam tokens do autor: só rodar com `LIFEAIM_REAL_AI=1` e depois de ele autorizar.
8. Baixar qualquer arquivo exige pedir antes ao autor, dizendo nome, origem e tamanho.
9. Uma fase por commit, e só commitar quando o autor pedir. Sem `git stash`, `reset` ou troca de branch.

## Onde as coisas estão

| O quê | Onde |
|---|---|
| Motor da conversa | `src/Modules/LifeAim.Modules.Agents/Application/Workflow/PippinWorkflow.cs` |
| Roteamento de mensagens fora do esperado | `.../Workflow/PathClassifier.cs` |
| Etapa "entre tarefas" | `.../Nodes/CollectGoalsNode.cs` (`Stage.CollectGoals`) |
| Agente do diário e suas ferramentas | `.../Application/Fitness/FitnessAgent.cs`, `FitnessTools.cs`, `FitnessText.cs` |
| Contrato do diário | `src/Modules/LifeAim.Modules.Fitness/Contracts/IFitnessJournal.cs` |
| Acompanhamento diário (modelo de job proativo) | `.../Application/FollowUp/DailyFollowUpJob.cs`, `FollowUpPlanner.cs` |
| Envio pelo Discord | `src/Modules/LifeAim.Modules.Communication/Infrastructure/DiscordBot/` |
| Testes da conversa do diário | `tests/LifeAim.Modules.Agents.Tests/FitnessConversationTests.cs` |

---

## Fase 1 — Tabela TACO como referência de alimentos

**Objetivo.** As calorias de alimentos comuns passam a vir de uma tabela, não da memória do modelo.

**Evidência.** No teste real, "torrada integral" foi estimada como fatia de pão de forma (~75 kcal). Hoje a ordem é: valor dito pelo usuário → tabela pessoal → estimativa da IA.

**Passos.**
1. Pedir ao autor permissão para baixar a TACO 4ª edição (NEPA/Unicamp, 597 alimentos, valores por 100 g), informando nome do arquivo, origem e tamanho. Não digitar valores de memória: um número errado apresentado como "TACO" é pior que uma estimativa declarada.
2. Criar `fitness.reference_foods` (id, nome, nome normalizado sem acento e em minúsculas, categoria, kcal, proteína, carboidrato, gordura e fibra por 100 g, fonte). Semear por migration SQL gerada a partir do arquivo baixado.
3. No contrato, `SearchReferenceFoodsAsync(query, limit)`: busca por todas as palavras da consulta no nome normalizado, devolvendo no máximo 8 linhas. Não depender de extensões do PostgreSQL que possam não existir no servidor.
4. Em `FitnessTools`, ferramenta `search_food(query)`. O resultado traz nome completo da TACO (que distingue cru de cozido) e os valores por 100 g.
5. Novo valor de origem `reference_table` em `NutritionSource`, na coluna `source` (nova migration alterando o CHECK) e em `FitnessText`.
6. Em `FitnessAgent`, a ordem vira: usuário → tabela pessoal → TACO → estimativa. Instruir a buscar só os itens que não estão na tabela pessoal, em uma chamada por item, e a escolher a linha "cozido/pronto" quando o usuário pesou a comida pronta.

**Pronto quando.**
- Teste de integração: buscar "arroz cozido" devolve a linha de arroz tipo 1 cozido com os valores do arquivo.
- Teste de conversa: um item achado na TACO é salvo com origem `reference_table` e calorias proporcionais à quantidade.
- A migration aplica em banco vazio (o CI roda isso).

---

## Fase 2 — Lembretes proativos do diário

**Objetivo.** O Pippin toma a iniciativa no diário, como já faz no acompanhamento das metas.

**Evidência.** O autor falhava vários dias em registrar. Hoje o diário só reage; o "lembrete da vitamina" é uma frase no prompt, sem horário.

**Comportamento.**

| Lembrete | Dispara quando | Não dispara se |
|---|---|---|
| Pesagem | chega o horário escolhido | já há peso no dia |
| Dia sem registro | 14h sem nenhuma refeição no dia | já há refeição |
| Fechamento do dia | horário escolhido (padrão 21h30) | não há nada registrado no dia |
| Lembrete livre ("vitamina") | horário escolhido | já foi confirmado no dia |

**Regras.**
- Tudo é opcional e ligado por conversa ("me lembra de pesar às 8h", "para de me lembrar da vitamina").
- No máximo um envio por tipo por dia, e nunca reenviar depois de um restart.
- Respeitar o silêncio entre 22h e 9h, salvo horário que o próprio usuário escolheu (regra da Funcionalidade 3).
- Só enviar entre tarefas; no meio de um cadastro, esperar. O envio não muda a etapa da conversa.
- O fechamento do dia mostra os totais calculados por `FitnessText.Day` e é o único momento em que o saldo do dia é dito.

**Passos.**
1. Migration em `fitness`: `reminders` (usuário, tipo, horário, texto livre opcional, ativo) e `reminder_log` (usuário, dia, lembrete, enviado em; único por dia).
2. Contrato: listar lembretes devidos, salvar, desligar, marcar como enviado.
3. Ferramentas `set_reminder` e `remove_reminder` em `FitnessTools`; retirar do prompt a regra improvisada da vitamina.
4. Regras de decisão em código puro, no estilo de `FollowUpPlanner` (entrada: agora, lembretes, registro do dia, log; saída: o que enviar).
5. `FitnessReminderJob` (Quartz, a cada 5 minutos, `DisallowConcurrentExecution`), registrado em `AgentsModule`.
6. Em `PippinWorkflow`, um método de envio proativo que não muda a etapa, sob o mesmo lock por usuário.
7. Textos: pesagem, dia sem registro e lembrete livre são fixos em código (sem custo de IA); só o fechamento do dia é escrito pela IA.

**Pronto quando.** Testes das regras cobrem: dispara no horário, não repete no dia, não dispara se a condição já foi atendida, respeita o silêncio, espera quando o usuário está ocupado, e a resposta "tomei" ao lembrete livre o marca como feito.

---

## Fase 3 — Velocidade das respostas

**Objetivo.** Uma refeição registrada em menos de 8 segundos, na mediana.

**Evidência.** Teste real: cerca de 20 segundos por mensagem. Uma mensagem do diário entre tarefas passa por até três chamadas de IA em fila: `CollectGoalsNode` (extração de metas) → agente (chamada da ferramenta) → agente (resposta). Fora de `CollectGoals`, entra ainda o `PathClassifier`.

**Hipóteses a confirmar antes de mudar.**
- O modelo padrão (`OpenAIOptions.Model`) é de raciocínio e nenhuma chamada define o esforço de raciocínio; os nós só classificam ou extraem.
- A maior parte do tempo está na chamada do agente com o prompt longo.

**Passos.**
1. Medir primeiro: log de duração e de tokens por chamada de IA, com o nome do nó ou agente. Rodar o teste real uma vez (com autorização) e anotar a linha de base.
2. Permitir configuração por papel: modelo e esforço de raciocínio separados para nós/classificador e para os agentes. Conferir na versão instalada de `Microsoft.Extensions.AI` como isso se passa; não presumir a API.
3. Criar uma etapa ociosa de verdade. Hoje "entre tarefas" é `Stage.CollectGoals`, então toda mensagem paga uma extração de metas. Com `Stage.Idle`, uma chamada curta decide: meta nova, conversa sobre metas, diário, cumprimento ou fora de escopo. `Stage` é salvo por nome em `agents.conversations`: exige migration de dados e revisão de todos os `CanStart…` do `PippinWorkflow`.
4. Carregar em paralelo o que o prompt do diário lê do banco (perfil, tabela, dias).
5. Encurtar o prompt do diário: os últimos 14 dias só entram quando a mensagem é pergunta; para um registro, bastam hoje e a tabela pessoal.

**Pronto quando.** A linha de base e o resultado ficam anotados na descrição do commit, a mediana cai para menos de 8 s no teste real, e todos os testes existentes passam sem mudança de comportamento.

---

## Fase 4 — Conversa menos burocrática

**Objetivo.** Menos mensagens que soam como sistema.

**Evidência (código).**
- `FallbackRouter.Route` responde "não achei essa funcionalidade" a tudo que o classificador não reconhece, com piada sorteada.
- `WithPendingQuestion` repete "Mas voltando ao que eu tinha perguntado: …" ao fim de cada resposta fora do roteiro.
- `GreetingReply` lista todas as metas a cada "oi".
- O acompanhamento diário pergunta "foi à academia?" mesmo quando o treino já está no diário sem número de meta.

**Passos.**
1. Fora de escopo: uma frase curta dizendo o que o Pippin faz (metas e diário), sem piada repetida. Atualizar o item 9 da página de Experiência no Notion se o autor concordar com a mudança de tom.
2. Pergunta pendente: repetir no máximo uma vez a cada três mensagens fora do roteiro, e nunca duas vezes seguidas com o mesmo texto.
3. Cumprimento: com diário ativo, responder com o estado do dia ("hoje: 2 refeições, ~740 kcal") em vez da lista de metas.
4. Acompanhamento: antes de perguntar sobre uma meta de treino ou de peso, olhar o diário do dia; se já houver registro, marcar como feito sem perguntar.
5. Depois de `log_exercise` ou `log_body` sem número de meta, o código tenta ligar à meta pelo que já foi ligado antes (guardar no perfil qual meta é a de peso e qual a de treino).

**Pronto quando.** Há teste para cada um dos cinco comportamentos, e o teste real do diário continua passando.

---

## Fase 5 — Diário dentro dos resumos e da análise de prioridades

**Objetivo.** O resumo mensal e o panorama trimestral (Funcionalidade 4) falam de peso, calorias e treino.

**Evidência.** `Reporting` só conhece `Progress` e os check-ins. O peso entra como progresso em "kg", e os totais somam quilos (`GoalTools.ProgressSummary`), o que não faz sentido para peso.

**Passos.**
1. `IFitnessJournal` ganha um resumo de período (dias registrados, médias, variação da média de peso de 7 dias, dias de treino). Regras em código, com testes.
2. `SummaryWriter` recebe esse resumo pela porta de `Agents`, sem `Reporting` depender de `Fitness`.
3. Em `GoalTools`, unidades de medida de estado (kg, cm, %) mostram primeiro, último e variação, não soma.
4. Insight opcional do mês pode vir do diário ("seus dias acima da meta quase sempre têm lanche depois das 22h"), sempre como observação calculada, nunca inventada.

**Pronto quando.** Um mês com diário gera resumo com peso inicial, final e média de calorias; um mês sem diário gera o resumo de hoje, sem mudança.

---

## Fase 6 — Importar o histórico do autor

**Objetivo.** O diário começa com janeiro a junho de 2026, e não vazio.

**Passos.**
1. Ferramenta de linha de comando separada (`src/LifeAim.Importer`), fora do deploy, que lê o arquivo de texto do autor de um caminho passado por argumento.
2. Seção 2 (peso, medidas, bioimpedância) e seção 3 (tabela nutricional) são regulares: importar por código.
3. Seção 1 (alimentação) tem formato livre: extrair com a IA, dia por dia, usando os totais já escritos no arquivo como conferência. Dias cuja soma diverge mais de 15% vão para um relatório, sem importar.
4. Modo de simulação por padrão: mostra o que seria gravado; grava só com confirmação.
5. O arquivo do autor nunca é copiado para o repositório nem para os testes; os testes usam um trecho fictício.

**Pronto quando.** A simulação lista os dias reconhecidos e os divergentes, e rodar duas vezes não duplica nada.

---

## Fase 7 — Fotos de prato e de rótulo

**Objetivo.** Mandar a foto do rótulo em vez de digitar a tabela nutricional.

**Evidência.** `DiscordBotService` só repassa `message.Content`; anexos são ignorados.

**Passos.**
1. `PippinMessage` ganha anexos de imagem (bytes e tipo), com limite de tamanho e de quantidade.
2. Só o agente do diário recebe imagens. Rótulo → `save_food`; prato → estimativa com faixa larga, dita como estimativa de foto.
3. Imagens não são guardadas: usadas na chamada e descartadas.

**Pronto quando.** Teste com cliente de IA falso confirma que a imagem chega ao agente; teste real (com autorização) lê um rótulo fictício.

---

## Fase 8 — Especialidade como parte da meta

**Objetivo.** Cumprir o conceito "meta com especialidade": o diário pertence a uma meta, e outras especialidades podem vir depois.

**Evidência.** Hoje o diário abre para qualquer usuário que passou da apresentação (`PathClassifier.OffersFitness`), sem ligação formal com a meta.

**Passos.**
1. Coluna `specialty` em `goals.goals` (nula ou `weight_loss`), exposta em `GoalDto`.
2. Ao salvar ou detalhar uma meta, a IA propõe a especialidade e o código confirma com o usuário em uma frase.
3. Ao ativar, uma conversa curta de perfil: altura, idade, sexo, peso alvo e rotina, uma pergunta por vez, podendo pular.
4. `OffersFitness` passa a exigir uma meta ativa com a especialidade; pausar ou encerrar a meta desliga os lembretes da Fase 2.

**Pronto quando.** Usuário sem a especialidade não é levado ao diário, e o autor (que já usa) é migrado sem perder nada.

---

## Fase 9 — Documentação no Notion

Só com o pedido do autor, e sem dados pessoais.
- Página "01 — Funcionalidades": seção "07 — Metas com especialidade: emagrecimento".
- Base "Funcionalidades": linha nova com categoria, release e status.
- Página "04 — Arquitetura": módulo `Fitness`, schema `fitness`, e a decisão de os agentes com ferramentas ficarem em `Agents`.
- Página "03 — Ideias": riscar o que virou funcionalidade.

---

## Ordem sugerida

1. Fase 1 (TACO) e Fase 2 (lembretes): pedidas pelo autor, independentes entre si.
2. Fase 3 (velocidade): o que mais pesa no uso diário.
3. Fase 4 (conversa), depois Fase 5 (resumos).
4. Fases 6, 7 e 8 conforme o interesse do autor.
5. Fase 9 ao fim de cada fase aceita.

---

## Resultado da execução (17/11/2026)

As nove fases foram implementadas na branch `feature/goal-intake`. Testes sem IA: 313 passando. Testes com a IA real
do diário (`FitnessRealAiTests`): 2 passando.

| Fase | Situação | O que ficou diferente do plano |
|---|---|---|
| 1. TACO | Feita | 591 alimentos (6 linhas sem calorias ficaram de fora). A IA indica a linha e a quantidade; o código busca os valores e faz a conta. As linhas parecidas com a mensagem já vão no prompt, para evitar uma rodada de busca. |
| 2. Lembretes | Feita | Como desenhado. O fechamento do dia é o único texto escrito pela IA. |
| 3. Velocidade | Feita | Mediana de ~22 s para ~7 s por mensagem no teste real. Não foi criada a etapa `Idle`: o ganho veio do esforço de raciocínio baixo, do prompt com a parte fixa primeiro (cache), e de pular a triagem enquanto a conversa está no diário (90 min). Esforço mínimo nos agentes foi testado e descartado. |
| 4. Conversa | Feita | A pergunta pendente deixa de se repetir só nas respostas dos agentes; no roteador ela continua, porque ali o usuário não recebeu resposta. |
| 5. Resumos | Feita | O diário só enriquece um resumo que já existiria; um mês sem acompanhamento de metas continua sem resumo. |
| 6. Importador | Feito | Testado com arquivo fictício. No arquivo real, só a seção corporal foi simulada (44 registros lidos, nada gravado). A importação real fica para o autor rodar. |
| 7. Fotos | Feita | Uma mensagem com foto vai direto ao agente do diário. |
| 8. Especialidade | Feita | A ligação fica no perfil do módulo Fitness, não em uma coluna de `goals.goals`: o módulo Goals continua sem conhecer especialidades. A ativação é automática e silenciosa (pela meta), sem pergunta de confirmação. |
| 9. Notion | Feita | Funcionalidade 07, linha na base, arquitetura e duas ideias atualizadas. |

### Ainda sem conferência

- O esforço de raciocínio dos nós passou do padrão do modelo para "baixo". Os testes com a IA real do cadastro de
  metas (`PippinRealAiTests`) não foram rodados depois dessa mudança.
- Lembretes, fotos pelo Discord e a divisão de mensagens longas não foram testados no Discord de verdade.
- Nada foi publicado: as migrations `fitness` 0001 a 0004 só foram aplicadas no banco local.
