# Portal do Distribuidor: incidente de ontem

> **Repositório:** https://github.com/GabrielCampos07/incidente-janela-corte

Isso aqui é um documento de raciocínio, não uma implementação do portal. Código só aparece no trecho mínimo que sustenta cada argumento.

## Resumo

- **Antes das 14h30 de hoje:** só mexo no que dá pra desfazer em minutos e sem deploy de código (no máximo uma revisão nova de configuração da task definition). Congelo deploy, seguro a prévia de enquadramento, subo a API de 2 pra 4 tasks com o pool caindo pra 50 por task (mesmo teto de 200 conexões), passo o visibility timeout da fila pra 180 s, garanto os access logs do API Gateway com o horário de chegada e fico em sala de guerra até 14h45.
- **Causa provável (ainda é hipótese):** a prévia multiplicou consulta cara de posições, o banco saturou, o pool esgotou e o POST /ordens ficou na fila atrás da prévia. Sem idempotência, cada clique repetido virou ordem duplicada. Como o horário é gravado depois do commit, a lentidão jogou ordem pra D+1. No integrador, visibility timeout menor que a chamada gerou reenvio, webhook repetido e a DLQ.
- **Os 37 casos:** os access logs do API Gateway registram a chegada de cada requisição, independente do commit. Uso isso como prova pra classificar ordem por ordem, e vou atrás também de quem foi afetado e não reclamou.
- **Correção de raiz:** horário do corte pego na chegada, chave de idempotência com outbox, consulta reescrita com a prévia em lote, integrador idempotente com encerramento gracioso e teste de carga do pico como gate de release.
- **O que eu decidi não fazer agora** tá escrito em cada seção ("O que não faço antes da janela", "O que decido não fazer").

---

## 1) Investigação antes da solução

### Primeiro a linha do tempo, depois a hipótese

Monto um painel só no CloudWatch, minuto a minuto, de 13h30 a 15h00 de ontem, com:

- quantidade de requisições por rota no API Gateway, separando a prévia do POST /ordens;
- `Latency`, `IntegrationLatency` e `5XXError` do POST /ordens;
- `HTTPCode_ELB_502_Count` e `UnHealthyHostCount` do ALB (suposição minha: o cenário só cita o API Gateway, então assumo um ALB atrás do VPC link; se não tiver, uso só as métricas do Gateway e do ECS);
- CPU e memória das tasks no ECS;
- `CPUUtilization`, `DatabaseConnections` e `DBLoad` do RDS;
- `NumberOfMessagesSent`, `NumberOfMessagesReceived`, `NumberOfMessagesDeleted` e `ApproximateAgeOfOldestMessage` da fila, e o tamanho da DLQ;
- marcação vertical em 13h50 (deploy), 14h12 (início da degradação) e 14h30 (corte).

A série que mexe primeiro tá mais perto da causa. Sem essa visão, cada hipótese vira achismo.


### Hipóteses para a degradação do endpoint e o esgotamento do pool

**H1. A prévia de enquadramento multiplicou a carga.**
- **Onde eu olho primeiro:** contagem por rota nos access logs do API Gateway (Logs Insights, `stats count() by resourcePath, bin(1m)`), comparando 14h00–14h30 de ontem com o mesmo intervalo de três semanas atrás, antes da prévia. No Performance Insights, aba Top SQL, vejo se é a consulta da prévia ou a de posições que tá puxando a carga do banco.
- **Confirma:** a prévia com várias vezes o volume do POST e a consulta dela no topo.
- **Elimina:** volume da prévia estável e outra consulta no topo.
- **A pergunta que importa:** a prévia tá no ar há duas semanas, então o que mudou ontem? Olho o p95 entre 14h e 14h30 em cada dia útil desde então. Se foi subindo aos poucos, é carga; se deu um salto em 22/10, é o deploy.

**H2. A consulta de posições tem custo unitário alto.**
- **Onde olho:** no Top SQL, chamadas, tempo médio e linhas por chamada (de `pg_stat_statements`), e o evento de espera dominante (CPU ou `IO:DataFileRead`). No ECS, memória das tasks, porque materializar grafo de entidade pesa na aplicação também.
- **Confirma:** linhas por chamada ordens de grandeza acima do que a tela exibe.
- **Combina com H1:** H1 é o multiplicador, H2 é o custo de cada chamada.

**H3. Conexões presas pela aplicação.** DbContext com tempo de vida errado, transação aberta durante chamada externa, `.Result` ou `.Wait()` esgotando o thread pool.
- **Onde olho:** `DatabaseConnections` comparado com o orçamento (2 tasks × 100, o padrão do Npgsql, igual a 200), e `idle in transaction` no `pg_stat_activity`.
- **Confirma:** conexões no teto com o CPU do RDS baixo.
- **Descarta em parte:** o RDS ficou em 96% de CPU, o que aponta pra consulta cara como causa principal. H3 pode estar junto, mas não explica a CPU.

**H4. Contenção de lock.** O integrador atualizando status enquanto a API insere.
- **Onde olho:** esperas `Lock:transactionid` ou `Lock:tuple` no Performance Insights.
- **Descarta:** se for lock, CPU não é o gargalo. Ou é isso, ou é H1/H2 como causa principal.

**H5. Outro cliente no banco no mesmo horário**, como a Lambda de relatórios ou um job agendado.
- **Onde olho:** carga do Performance Insights por usuário e host do banco, e invocações e duração da Lambda.

**H6. Cascata de health check.** Se o health check consulta o banco, o pool esgotado faz a task ser marcada como unhealthy, o ECS a mata e a capacidade cai pela metade.
- **Onde olho:** eventos do serviço no ECS, `UnHealthyHostCount` e o 502 do ALB.
- **Por que importa:** é a que melhor explica os 502. O 504 é o API Gateway desistindo aos 29 segundos; o 502 é o destino morrendo ou fechando a conexão.

**H7. Saturação das tasks** por CPU, memória ou OOM. Compatível com H2.

### Hipóteses para o acúmulo na fila e as mensagens na DLQ

**H8. Visibility timeout menor que o processamento.** Aos 30 segundos a mensagem volta e é processada de novo, com a primeira execução ainda rodando.
- **Onde olho:** razão entre `NumberOfMessagesReceived` e `NumberOfMessagesSent` bem acima de 1; `ApproximateReceiveCount` nas mensagens da DLQ; a mesma ordem processada por tasks diferentes nos logs do integrador.

**H9. Sistema de processamento lento.** Latência das chamadas de saída do integrador subindo junto com `ApproximateAgeOfOldestMessage`.

**H10. Webhooks síncronos no mesmo handler.** Um endpoint lento de distribuidor alonga o processamento além do visibility timeout. Compatível com H8 e H9.

**H11. Processamento interrompido no deploy.** Concentração de mensagens na DLQ logo após o deploy do integrador.

**H12. Mensagens inválidas.** Erros de validação nas mensagens da DLQ. H12 são mensagens que nunca vão funcionar; H8 são mensagens que provavelmente funcionaram mais de uma vez. O tratamento é o oposto.

**Uma coisa que não fecha e precisa de explicação antes de concluir:** 40 mil mensagens contra uma média de 22 mil ordens por dia. Ou ontem foi fora da curva, ou alguém leu `NumberOfMessagesReceived` (que conta reentregas) como backlog, ou a API publica mais de uma vez por ordem, o que aponta pra dupla escrita entre o commit e a publicação no SQS. Comparo `NumberOfMessagesSent` do dia com a contagem de ordens criadas no banco.

### O deploy das 13h50

Primeiro vejo **qual serviço** subiu (API, front ou integrador) pelos eventos do ECS. Depois leio o diff no Git, as migrations aplicadas e o diff da task definition (pool, timeouts, health check, `stopTimeout`).

- **Torna o deploy causa provável:** degrau de latência em 13h50 com o mesmo volume; consulta nova no Top SQL; diff tocando consulta, prévia, pool, índice ou health check; em homologação, a versão nova degrada com o perfil do pico e a anterior não.
- **Descarta o deploy:** latência acompanhando volume, não horário; mesmas consultas com o mesmo custo; diff fora do caminho crítico; p95 já subindo nos dias anteriores.
- **De todo jeito:** o deploy reiniciou as tasks (pool e cache vazios) e, se pegou o integrador, matou mensagem no meio do processamento. Mesmo sem ser a causa, piorou tudo a 20 minutos do pico.

### Decisão sob pressão

**Meu critério:** até as 14h30 só faço o que dá pra desfazer em minutos, não pede deploy de código no caminho crítico, diminui a chance ou o estrago de repetir o incidente e dá pra acompanhar em tempo real. Qualquer mudança de código no caminho da ordem fica pra depois da janela, com teste e homologação.

**O que faço até a janela, nesta ordem:**
1. **Congelo deploys** em todos os serviços até as 15h.
2. **Seguro a prévia.** Desligo a feature flag, se tiver; se não tiver, coloco throttling na rota da prévia no API Gateway. Antes confirmo com o PO que a validação que vale é a do envio. Prefiro perder a prévia do que perder o envio.
3. **Garanto os access logs do API Gateway** com `requestTimeEpoch`. Se travar de novo hoje, tenho a prova da chegada de cada ordem.
4. **Visibility timeout pra 180 s.** É configuração da fila, sem deploy, e corta a reentrega que gera duplicidade. Vale só pras próximas mensagens recebidas (o que já tá em voo mantém o timeout antigo), e confiro se o integrador não sobrescreve o valor no `ReceiveMessage`.
5. **Escalo a API de 2 pra 4 tasks com `Max Pool Size=50`**: 4 × 50 = 200, o mesmo teto de hoje. Ganho capacidade de requisição e aguento melhor a cascata de health check sem jogar mais concorrência num banco saturado. É só uma revisão de configuração da task definition, sem código; o freio de verdade continua sendo o passo 2.
6. **Rollback do deploy das 13h50** só se o diff tocou o caminho crítico, não houve migration e o rollback é apenas trocar a revisão da task definition.
7. **Aviso Relacionamento:** se a tela não responder, o distribuidor confere a lista de ordens antes de reenviar.
8. **Sala de guerra das 13h45 às 14h45**, com limite combinado antes: se o p95 do POST passar de 1 s, bloqueio a prévia de vez.

**O que não faço antes da janela:**
- redrive da DLQ, que ia reenviar ordem que talvez já foi processada;
- aumentar o Max Pool Size, que só joga mais consulta simultânea num banco saturado;
- escalar o RDS verticalmente, que provoca failover;
- corrigir horário do corte ou idempotência na correria;
- mexer no `registrada_em` das 37 ordens: a correção vem por lançamento de ajuste auditável.

### O que respondo hoje para Relacionamento

> Confirmamos instabilidade no portal ontem a partir das 14h12, e ela pode ter feito ordens enviadas antes das 14h30 serem registradas com horário posterior. Temos um registro independente do horário de chegada de cada requisição (os logs de entrada da AWS, que não dependem do processamento) e estamos cruzando as ordens uma a uma. Até amanhã às 12h entregamos cada uma das 37 classificada em três grupos: recebida antes do corte, recebida depois, ou tentativa antes do corte com reenvio depois. Para as recebidas antes do corte, a recomendação técnica é tratá-las como D0; a forma de reparação será definida com Operações e Compliance. Também vamos procurar ativamente outras ordens na mesma situação que ainda não geraram reclamação. A causa ainda está em apuração, e não atribuiremos a falha ao distribuidor sem evidência.

Deixo de fora de propósito três coisas: prometer ressarcimento (não é decisão de engenharia), cravar a causa (ainda não tá confirmada) e dar a entender que o distribuidor errou.

---

## 2) Backend .NET, Entity Framework e PostgreSQL

### Da consulta ineficiente ao 502

1. **O banco trabalha demais.** `Include` em coleções gera JOIN com produto cartesiano; filtrar depois do `ToList` faz o banco ler e mandar tudo; e o EF ainda rastreia cada entidade.
2. **Cada consulta segura a conexão por mais tempo.** Pela Lei de Little, conexões ocupadas = chamadas por segundo × duração. Exemplo: com 300 chamadas por segundo da prévia a 20 ms, ficam ~6 conexões ocupadas. Com o banco saturado e cada chamada em 600 ms, são 180, perto do teto de 200.
3. **Banco saturado vira bola de neve.** Mais consulta simultânea brigando pela mesma CPU, e cada uma demora mais.
4. **O pool esgota.** A próxima requisição espera até o `Timeout` de aquisição do Npgsql (15 s por padrão) e falha com "connection pool has been exhausted".
5. **O POST /ordens divide o mesmo pool.** Um insert de 5 ms fica esperando atrás das consultas da prévia, porque não tem isolamento entre leitura e escrita. É assim que um endpoint barato chega a 9 s de p95.
6. **O Gateway desiste.** Aos 29 s, o API Gateway devolve 504. Se o health check depende do banco, as tasks morrem e aparecem os 502. Só que o backend continua trabalhando e às vezes faz o commit depois que o usuário já recebeu erro.

**Por que estoura perto do corte:** as ordens não chegam distribuídas; o pessoal junta boleta e manda tudo entre 13h45 e 14h30. A prévia multiplica esse volume pelo número de linhas e edições, e o clique repetido depois de cada timeout multiplica de novo. Em teoria de filas, a latência cresce de forma não linear quando a utilização se aproxima de 100% (aproximadamente 1/(1−ρ)): com 70% de CPU parece tudo normal, com 95% explode. Por isso não aparece de manhã.

> **Da minha experiência.** No leo-chiasso (Angular + Nest + Prisma no Neon), tive que separar a URL do pooler (`pgbouncer=true`) da conexão direta usada nas migrations e colocar retry na conexão em cold start. Em outro commit, troquei lookups sequenciais do cadastro de produto por `Promise.all`, com um teste garantindo comportamento e erros idênticos. O que eu trago pra cá: conexão é orçamento, e cada tipo de uso precisa do seu pedaço bem definido. É o mesmo motivo de separar pool de leitura e de escrita no portal.

### Reescrita da consulta de posições

O padrão que o cenário descreve:

```csharp
var posicoes = await _db.Posicoes
    .Include(p => p.Fundo).ThenInclude(f => f.Classe)
    .Include(p => p.Cotista)
    .Include(p => p.Movimentacoes)            // coleção: produto cartesiano
    .Where(p => p.DistribuidorId == distribuidorId)
    .ToListAsync();                           // materializa e rastreia tudo

return posicoes
    .Where(p => p.Fundo.Ativo && p.Saldo > 0 && (fundoId == null || p.FundoId == fundoId))
    .Select(p => new PosicaoDto(/* ... */))
    .ToList();
```

Como eu reescreveria, no nível que levo pra code review:

```csharp
public async Task<Pagina<PosicaoDto>> ListarAsync(Guid distribuidorId, FiltroPosicao filtro, CancellationToken ct)
{
    var tamanho = Math.Clamp(filtro.Tamanho, 1, 200);

    var query = _db.Posicoes
        .AsNoTracking()
        .Where(p => p.DistribuidorId == distribuidorId && p.Saldo > 0 && p.Fundo.Ativo);

    if (filtro.FundoId is { } fundoId)
        query = query.Where(p => p.FundoId == fundoId);

    if (filtro.AposId is { } aposId)          // paginação por cursor
        query = query.Where(p => p.Id > aposId);

    var itens = await query
        .OrderBy(p => p.Id)
        .Select(p => new PosicaoDto(
            p.Id, p.FundoId, p.Fundo.Nome, p.Fundo.Classe.Codigo,
            p.CotistaId, p.QuantidadeCotas, p.Saldo, p.DataReferencia))
        .Take(tamanho + 1)
        .ToListAsync(ct);

    return Pagina.De(itens, tamanho);
}
```

```sql
-- migration com suppressTransaction: CONCURRENTLY não roda dentro de transação
-- listagem com filtro de fundo
CREATE INDEX CONCURRENTLY ix_posicoes_distribuidor_fundo_id
    ON posicoes (distribuidor_id, fundo_id, id)
    INCLUDE (saldo, quantidade_cotas)
    WHERE saldo > 0;

-- listagem sem filtro de fundo, ordenada por id (o caso mais comum)
CREATE INDEX CONCURRENTLY ix_posicoes_distribuidor_id
    ON posicoes (distribuidor_id, id)
    INCLUDE (fundo_id, saldo, quantidade_cotas)
    WHERE saldo > 0;
```

O primeiro não ajuda a paginação por `id` quando não tem filtro de fundo, por isso o segundo. Dois índices custam escrita em `posicoes`; no `EXPLAIN` com volume parecido com o de produção eu vejo se os dois se pagam ou se um basta.

O que eu cobro no review:
- **Projeção:** um SELECT só com os JOINs que precisa, sem grafo de entidade.
- **Filtro no banco** e sem rastreamento de entidade.
- **Cancelamento:** `CancellationToken` propagado.
- **SQL gerado:** confiro com `ToQueryString()` e `EXPLAIN (ANALYZE, BUFFERS)` em homologação com volume parecido com o de produção.
- **A prévia em si:** não precisa de lista, precisa do saldo agregado por cotista e fundo. Um `GroupBy` com `Sum` no banco, chamado uma vez por grade e não por linha, derruba o custo em ordens de grandeza.

### O resto das intervenções

| Intervenção | Causa ou sintoma | Ganho | Risco | Esforço |
|---|---|---|---|---|
| Projeção, filtro no banco, `AsNoTracking` | Causa | Alto | Baixo: checar filtros em memória não traduzíveis | Baixo |
| Índices parciais com `CONCURRENTLY` | Causa | Alto se hoje há seq scan | Insert de ordens um pouco mais caro | Baixo |
| Paginação por cursor | Causa: limita o custo por requisição | Médio | Muda o contrato da API | Médio |
| Prévia em lote, debounce, cache curto | Causa: elimina a amplificação | Muito alto | Prévia alguns segundos defasada | Médio |
| DbContext por requisição (`AddDbContextPool`) | Causa, se H3 se confirmar | Alto nesse caso | Baixo | Baixo |
| Async de ponta a ponta, sem `.Result` | Causa, se houver starvation do thread pool | Médio | Baixo | Médio |
| Pools separados pra escrita e leitura, `statement_timeout` na leitura | Isola o caminho crítico | Alto | Exige orçamento de conexões | Baixo |
| Réplica de leitura pra prévia e posições | Mitigação | Alto | Atraso de replicação e custo | Médio |
| Rate limit por distribuidor | Mitigação | Médio | Calibração errada bloqueia tráfego legítimo | Baixo |
| Aumentar Max Pool Size | **Sintoma, e pode piorar** | Negativo com banco saturado | Mais concorrência no RDS | Baixo |
| Autoscaling do ECS | Sintoma, se o gargalo é o banco | Protege as tasks | Mantenho tasks × pool constante (4 × 50) | Baixo |
| RDS maior ou RDS Proxy | Sintoma, compra folga | Médio | Custo mensal; Proxy não resolve CPU | Médio |

Sobre custo (FinOps): em vez de deixar mais task ligada o dia inteiro, uso scaling agendado das 13h30 às 15h00, com mínimo de 3 tasks em mais de uma zona de disponibilidade. Réplica ou RDS maior só entra se, depois de corrigir a consulta, o Performance Insights ainda mostrar o banco sem folga. Corrigir a consulta sai praticamente de graça; instância maior é custo todo mês.

### O número que mostra que funcionou

- **Pra a causa:** no Top SQL do Performance Insights, a carga (AAS) e o tempo médio por chamada da consulta de posições e da prévia no pico, multiplicados pelas chamadas por segundo.
- **Pra o efeito:** p95 e p99 do POST /ordens entre 14h00 e 14h30 na métrica `Latency` do API Gateway por recurso, e o pico de `DatabaseConnections`.
- **Antes de subir:** em homologação, k6 com o perfil do pico. Comparo a vazão máxima sustentada com p95 do POST abaixo de 300 ms, antes e depois.

---

## 3) Mensageria, idempotência e o horário do corte

### Como surgiram as 214 duplicadas

O POST chega e fica esperando conexão, ou faz o commit devagar. O Gateway devolve 504 aos 29 s, mas o backend segue e termina o commit. O usuário não sabe disso e clica de novo; sem chave de idempotência, cada requisição vira uma ordem. Some a isso o duplo clique (botão não desabilita) e cliente de integração que repete sozinho quando toma 5xx.

Parte das duplicadas são **ordens diferentes** (IDs distintos, criados pela API). Outras são **a mesma ordem mandada duas vezes** pelo integrador. Separo as duas pelo ID da ordem, porque a causa e o tratamento são diferentes.

### Desenho de idempotência

- **Onde nasce a chave:** no cliente, quando a boleta ou a linha é criada (`crypto.randomUUID()`), e não no clique; se nascer no clique, cada tentativa ganha chave nova e a proteção não serve pra nada. A chave fica no estado da tela e no `sessionStorage`. Pra quem integra por API, o header `Idempotency-Key` é obrigatório.
- **Escopo:** a combinação (distribuidor, chave) é única, com o hash do corpo guardado junto.
- **Tempo de vida:** 7 dias. Cobre ordem, corte, confirmação assíncrona e reconciliação, e o custo no Postgres é irrelevante. 24 horas é pouco pra ordem que atravessa o corte.
- **Mesma requisição de novo:** a API devolve o mesmo `201` com o mesmo corpo e `Idempotent-Replayed: true`.
- **Requisições simultâneas com a mesma chave:** a segunda não fica segurando conexão esperando: o `INSERT ... ON CONFLICT` roda com `lock_timeout` curto e, se a primeira ainda não deu commit, responde `409` com `Retry-After: 1`. O front consulta pela chave até ter resposta. Se a primeira já commitou, devolve o mesmo `201`.
- **Mesma chave com outro corpo:** `422`.
- **Duas ordens iguais de propósito:** chaves diferentes, porque o usuário criou nova linha ou usou "duplicar". Nunca uso hash do conteúdo como chave, porque ia bloquear ordem legítima. Ordem com mesmo cotista, fundo e valor em menos de 2 minutos pode pedir confirmação explícita; isso é regra de produto, combinada com o PO.

```csharp
await using var tx = await _db.Database.BeginTransactionAsync(ct);
await _db.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '500ms'", ct);

int inserida;
try
{
    inserida = await _db.Database.ExecuteSqlInterpolatedAsync($"""
        INSERT INTO idempotencia (distribuidor_id, chave, hash_payload, primeira_chegada)
        VALUES ({distribuidorId}, {chave}, {hash}, {recebimento.RecebidaEm})
        ON CONFLICT (distribuidor_id, chave) DO NOTHING
        """, ct);
}
catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.LockNotAvailable)
{
    // a primeira requisição com essa chave ainda não deu commit
    response.Headers.RetryAfter = "1";
    return Results.Problem(statusCode: 409, title: "Requisição com essa chave ainda em processamento");
}

if (inserida == 0)
{
    await tx.RollbackAsync(ct);
    return await ReplayAsync(distribuidorId, chave, hash, ct);   // mesmo 201, ou 422 se o corpo mudou
}

var ordem = Ordem.Criar(distribuidorId, req, recebimento.RecebidaEm, janelaDoFundo);
_db.Ordens.Add(ordem);
_db.Outbox.Add(OutboxMensagem.OrdemAceita(ordem));   // publicação no SQS feita depois do commit
await _db.SaveChangesAsync(ct);
await tx.CommitAsync(ct);
```

A outbox resolve um problema que ninguém citou: hoje a API grava no banco e publica no SQS em duas operações separadas. Se o commit passa e a publicação falha, a ordem nunca sai; se a publicação passa e o commit falha, sai ordem fantasma. Pode estar por trás das 40 mil mensagens.

### O integrador: visibility timeout de 30 s com chamada de 45 s

- **0 s:** o consumidor A recebe a mensagem e chama o processamento.
- **30 s:** a mensagem volta a ficar visível; o consumidor B a recebe com outro receipt handle e envia a mesma ordem de novo.
- **45 s:** A termina e tenta apagar com o handle antigo. Pela documentação do SQS, a chamada pode voltar sucesso sem apagar nada. A mensagem continua rodando.
- **Cada volta:** o `ApproximateReceiveCount` sobe e cada processamento completo dispara o webhook outra vez. Daí os webhooks repetidos.
- **Ao atingir `maxReceiveCount`:** a mensagem vai pra DLQ, mesmo já tendo sido processada duas ou três vezes. Por isso ninguém sabe dizer o estado das 1.900.
- **Na fila:** cada mensagem custa duas ou três vezes o trabalho, a vazão útil despenca e o backlog cresce.
- **No deploy:** o SIGTERM interrompe no meio, deixando ordens enviadas e não registradas, ou registradas sem webhook.

> **Da minha experiência.** No praiaki-digital, a fila de jobs em background roda no Postgres com `FOR UPDATE SKIP LOCKED`, concorrência configurável (padrão 4), lease de processamento que devolve jobs travados depois de 10 minutos (é o equivalente ao visibility timeout), backoff exponencial, status `dead` e janela de graça no SIGTERM. Cobranças no Asaas saem com `Idempotency-Key` e `externalReference` igual ao ID do pagamento, e a ordem é travada (`pg_advisory_xact_lock` + `FOR UPDATE`) pra não pagar duas vezes. Também peguei um risco de execução dupla: ao rodar os jobs dentro do processo web no Render, o mesmo loop não podia ligar no Compose, onde já existe um worker dedicado. Por isso a flag `RUN_INPROCESS_BACKGROUND_JOBS`. E no cliente Asaas da dwa-academia deixei explícito: sem retry automático em POST, porque POST não é idempotente.

### Como reescrevo o consumidor

- **Separar responsabilidades:** o integrador envia ao processamento e registra o resultado. A notificação vira evento numa outbox, consumida por um despachante separado, com retentativa por distribuidor, `X-Event-Id` estável pro distribuidor descartar repetidos e assinatura HMAC.
- **Concorrência:** limite fixo por task, calibrado pelo rate limit do sistema de processamento (pergunto pro fornecedor, não chuto). Long polling de 20 s, sem nunca puxar mais mensagem do que dou conta de processar.
- **Visibility:** 180 s na fila e heartbeat estendendo pra 120 s a cada 30 s.
- **Erros em três classes:**
  - **Permanente** (4xx de validação): marca a ordem como rejeitada e apaga a mensagem.
  - **Transitório** (conexão recusada, 429): devolve a mensagem com backoff exponencial e jitter, até 15 minutos.
  - **Ambíguo** (timeout depois de enviar): marca a ordem como `INCERTA` e, na próxima tentativa, consulta o destino antes de reenviar. É esse caso que gera duplicidade no destino.
- **Idempotência no destino:** reserva atômica no banco antes de enviar, ID da ordem como chave de idempotência no sistema de processamento (se ele aceitar) e consulta antes de reenviar.
- **Encerramento gracioso:** recebeu SIGTERM, para de buscar, espera as mensagens em andamento até o `stopTimeout` do ECS (configurado pra 120 s) e encerra. O Node precisa receber o sinal: `npm start` não repassa SIGTERM, então o container roda `node` direto, com `initProcessEnabled`.

```ts
async function handle(msg: SqsMessage): Promise<void> {
  const ordemId = JSON.parse(msg.body).ordemId as string;
  const heartbeat = setInterval(() => sqs.changeVisibility(msg.receiptHandle, 120), 30_000);
  try {
    const reserva = await repo.reservar(ordemId, WORKER_ID, 180);   // UPDATE condicional: só um worker por ordem
    if (!reserva) {
      if (await repo.estaFinalizada(ordemId)) await sqs.delete(msg.receiptHandle);
      return;
    }
    if (reserva.statusAnterior === "INCERTA") {
      const existente = await processamento.consultar(ordemId);
      if (existente) {
        await repo.marcarEnviada(ordemId, existente.protocolo);
        return void (await sqs.delete(msg.receiptHandle));
      }
    }
    const { protocolo } = await processamento.enviar(reserva.ordem, { idempotencyKey: ordemId });
    await repo.marcarEnviada(ordemId, protocolo);   // status + evento de webhook na mesma transação
    await sqs.delete(msg.receiptHandle);
  } catch (err) {
    if (err instanceof PermanentError) {
      await repo.marcarRejeitada(ordemId, err.message);
      await sqs.delete(msg.receiptHandle);
    } else {
      await (err instanceof AmbiguousError ? repo.marcarIncerta(ordemId) : repo.liberar(ordemId));
      await sqs.changeVisibility(msg.receiptHandle, backoffSeconds(msg.receiveCount));
    }
  } finally {
    clearInterval(heartbeat);
  }
}
```

**O que eu decido não fazer:** migrar pra fila FIFO. A deduplicação dela só vale pra envio dentro de 5 minutos e não impede reprocessamento por visibility timeout. A idempotência no consumidor vai ser necessária de qualquer jeito.

### O que faço hoje com as 1.900 mensagens da DLQ

1. **Nada de redrive, purge ou DeleteMessage.**
2. **Exporto agora pro S3**, com criptografia e acesso restrito. No SQS padrão, a expiração conta a partir do envio original, não da chegada na DLQ; com retenção de 4 dias, as mensagens podem sumir já na segunda-feira. Também subo a retenção da DLQ pra 14 dias.
3. **Classifico cada ordem** cruzando o status no banco, a presença e a contagem no sistema de processamento (extrato por ID externo) e o log de webhooks:
   - **Não processada:** reenvio pelo consumidor corrigido, depois da janela, em lotes pequenos.
   - **Processada uma vez:** atualizo o status e envio o webhook só se faltou.
   - **Processada duas ou mais vezes:** **urgente, hoje**. Operações cancela a duplicata antes do cálculo da cota.
   - **Processada com status desatualizado no portal:** corrijo com registro de auditoria.
   - **Mensagem sem ordem no banco:** publicação sem commit; investigo uma a uma.
4. **Amarro com as hipóteses:** concentração logo após o deploy aponta pra H11; `ApproximateReceiveCount` no limite com ordem já processada aponta pra H8.

As 214 duplicadas da API passam pela mesma matriz, separando as certas (mesma ordem mandada duas vezes) das prováveis (ordens diferentes com o mesmo conteúdo, que dependem do distribuidor confirmar antes de qualquer cancelamento).

### Onde pegar o horário do corte e o que fazer com os 37

**Onde pegar:** na chegada da requisição no API Gateway (`$context.requestTimeEpoch`), repassado ao backend por um header mapeado na integração, que sobrescreve qualquer valor enviado pelo cliente. Isso só vale como prova se o security group do serviço aceitar tráfego apenas do VPC link do Gateway; se der pra bater direto no serviço, alguém forja o header. Esse horário vira a coluna imutável `recebida_em`, separada de `registrada_em`, e a decisão D0/D+1 sai dela, no fuso `America/Sao_Paulo`, com calendário de dias úteis e horário de corte por fundo. Relógio do cliente, nunca.

```csharp
public DateOnly DataDeProcessamento(DateTimeOffset recebidaEm)
{
    var local = TimeZoneInfo.ConvertTime(recebidaEm, Brasilia).DateTime;
    var data = DateOnly.FromDateTime(local);
    if (!_calendario.EhDiaUtil(data) || TimeOnly.FromDateTime(local) >= _horarioCorte)
        return _calendario.ProximoDiaUtil(data);
    return data;
}
```

**Por que a implementação atual produz os 37:** o horário é gravado depois do commit. No dia a dia, a distância entre chegada e commit é de milissegundos. Ontem foi pra dezenas de segundos (espera pelo pool, consultas lentas, commits concluídos depois do 504). Uma ordem que chegou às 14h29m40s e terminou o commit às 14h30m10s foi pra D+1. Também confiro se tem `DateTime.Now` no código, porque no Fargate o fuso é UTC.

**Caso de borda pra Compliance:** a primeira tentativa chegou às 14h29, falhou sem gravar e o reenvio chegou às 14h31. Com a chave de idempotência, consigo registrar a primeira chegada. Minha recomendação é ficar do lado do cliente, porque a falha foi nossa, mas a política não é decisão minha.

**Corrigindo os 37:**
1. Pra cada ordem, procuro nos access logs do API Gateway (ou do ALB, se tiver) a primeira tentativa de POST correspondente e classifico: chegou antes do corte; tentou antes, falhou e reenviou depois; ou chegou depois.
2. **Abro a busca pra todas as ordens** registradas entre 14h30 e 14h45 com chegada antes do corte. Os 37 são os que reclamaram, não necessariamente todo mundo que foi afetado.
3. Pra cada ordem com direito a D0, entrego pra Operações e Compliance valor, cota de D0, cota de D+1 e diferença. A forma de reparação é decisão de negócio.
4. Não mexo no registro original. Toda correção vira lançamento de ajuste auditável: quem, quando, por quê e com qual evidência.

---

## 4) Qualidade de código, testes e uma conversa difícil

### Como eu conduzo o retorno

**Ordem e canal:**
1. **Conversa privada e síncrona**, por chamada ou presencial, antes de qualquer coisa. Não começo por comentário no PR nem pelo canal do incidente. Ele tem que ouvir de mim antes de ver o assunto no post-mortem.
2. **Post-mortem sem culpados**, tratando como falha de processo: um PR de 1.800 linhas aprovado sem discussão, sem teste de carga e sem feature flag.
3. **Comentários técnicos no PR de correção**, sobre o código e não sobre a pessoa.

Se rolar resistência, levo pro tech lead como questão de processo, não de pessoa.

**O que eu diria:**

> Queria falar contigo sobre a prévia antes do post-mortem, porque os dados apontam que ela teve papel na carga de ontem e prefiro que você saiba por mim. Não é sobre quem errou: o PR passou por revisão e o processo não pegou. Você conhece esse código e esse domínio melhor do que eu. O que levou à chamada por linha? Tinha prazo ou alguma restrição do front? Queria desenhar a correção junto, e acho que faz sentido você conduzir.

### Problemas estruturais que eu levantaria

- **Tamanho do PR:** 1.800 linhas não dá pra revisar. Interface, API, consulta e regra vieram juntas, e a aprovação sem discussão foi consequência, não azar.
- **Responsabilidade única (SRP):** o endpoint da prévia provavelmente consulta, aplica a regra de enquadramento e formata a resposta. Se a mesma regra existe na validação do envio, são duas implementações que podem divergir. Extraio uma `RegraDeEnquadramento` de domínio usada pelos dois.
- **Inversão de dependência (DIP):** controller dependendo direto do `DbContext` impede testar a regra sem banco e impede trocar a fonte por cache ou réplica. Introduzo uma porta `IConsultaPosicoes` com implementação de leitura.
- **Aberto/fechado (OCP):** regras por tipo de fundo em cadeias de `if`/`switch`. Uso uma estratégia por classe de fundo.
- **Segregação de interfaces (ISP):** repositório genérico devolvendo entidades com `Include` "por precaução" é exatamente o padrão que gerou a consulta cara. Cada uso ganha um modelo de leitura próprio.
- **Clean Code:** métodos longos, constantes fixas (o 14h30, que pode variar por fundo), ausência de `CancellationToken`, de timeouts e de métricas.

### Estratégia de testes

Os testes que teriam barrado o incidente, cada um na sua camada:

1. **Unidade (domínio):** a regra da janela de corte nos limites (14:29:59.999 e 14:30:00), com UTC, sexta-feira e feriado, e um teste com relógio falso que avança 45 s durante o processamento e verifica que a ordem continua em D0. É o teste que pegaria os 37.
2. **Integração com Postgres real (Testcontainers):** cinco POSTs simultâneos com a mesma chave criam uma ordem; mesma chave com outro corpo dá 422; duas ordens iguais com chaves diferentes são duas ordens. Na consulta de posições, um interceptor do EF garante um único comando e filtro aplicado no banco.
3. **Integração do consumidor**, com fila, banco e destino falsos ou LocalStack:
   - mensagem entregue duas vezes gera uma única chamada ao destino;
   - processamento mais longo que o visibility timeout é sustentado pelo heartbeat;
   - SIGTERM no meio não perde nem duplica;
   - timeout ambíguo consulta o destino antes de reenviar.
4. **Contrato:** webhooks com os distribuidores (formato, `X-Event-Id`, assinatura) e validação de schema contra a API do processamento.
5. **Carga:** k6 em homologação com o perfil do pico e a prévia ativa, como gate de release pra mudanças no caminho crítico. Reprova se o p95 do POST passar de 300 ms ou a taxa de erro passar de 0,1%.
6. **Front:** teste de componente garantindo botão desabilitado durante o envio, e um e2e com Playwright com a API respondendo em 10 s, verificando que nenhuma requisição duplicada sai.

**Por onde eu começo, sem parar o roadmap:**
- Cada correção deste incidente entra com seu teste de regressão. Prioridade: (1) janela de corte, maior risco de negócio e teste mais barato; (2) idempotência; (3) consumidor; (4) carga como gate; (5) contratos.
- Antes de refatorar legado, escrevo teste de caracterização, que trava o comportamento atual.
- O pipeline passa a exigir cobertura das linhas alteradas, não cobertura global. Meta global de 80% só gera teste vazio.

### Bônus: Next.js

> **Da minha experiência.** No dark-lashes (Next 15, App Router, checkout Stripe por Route Handler), o `BuyButton` desabilita o botão e mostra "Abrindo…" durante o `fetch`, e a chave secreta fica só no servidor. Isso resolve o duplo clique. Olhando de novo depois desse incidente, vejo o limite: em erro, o botão libera um novo clique sem chave de idempotência, e no portal é justamente esse reenvio às cegas depois do timeout que vira duplicata. Nas landings (git-landing, rocknfields, Next 16) uso `output: "export"` de propósito, porque são estáticas; dado de ordem e posição nunca pode ir por esse caminho.

- **Envio como máquina de estados:** `ocioso → enviando → aceita | rejeitada | verificando`. A `Idempotency-Key` nasce quando a linha da boleta é criada (não no clique) e fica no `sessionStorage`.
- **Timeout, 5xx e 409 nunca viram "tente novamente":** viram "verificando", com polling por chave (até 8 tentativas, com espera entre elas). Saiu `aceita` ou `rejeitada`, para; se esgotar, continua em "verificando" e o usuário pode reenviar, sempre com a mesma chave.
- **Recibo com horário oficial** ("recebida às 14h29m41s") vindo da API.
- **Prévia:** uma chamada por grade, no blur ou com debounce de 400–500 ms, `AbortController` pra respostas velhas e cache curto por cotista e fundo.
- **Cache:** dado privado é `cache: "no-store"`, em rota dinâmica e sem cache compartilhado.
- **Fronteira:** o front diminui a chance do problema; o backend garante a correção (idempotência, horário do corte, validação, rate limit) e nunca depende do front pra estar certo.

```tsx
"use client";

type Estado =
  | { tipo: "ocioso" }
  | { tipo: "enviando" }
  | { tipo: "verificando" }
  | { tipo: "aceita"; ordem: OrdemCriada }
  | { tipo: "rejeitada"; motivo: string };

const MAX_TENTATIVAS = 8;
const ESPERA_MS = 2_000;
const esperar = (ms: number) => new Promise((r) => setTimeout(r, ms));

export function useEnvioDeOrdem(chaveDaBoleta: string) {
  const [estado, setEstado] = useState<Estado>({ tipo: "ocioso" });
  const emAndamento = useRef(false);   // guarda síncrono: o estado só atualiza no próximo render

  // GET /api/ordens?idempotencyKey=...: aceita, rejeitada, ou null se a ordem ainda não existe
  async function verificar(): Promise<Estado> {
    for (let i = 0; i < MAX_TENTATIVAS; i++) {
      await esperar(ESPERA_MS);
      const resultado = await consultarPorChave(chaveDaBoleta).catch(() => null);
      if (resultado) return resultado;
    }
    return { tipo: "verificando" };   // não deu pra confirmar: o usuário pode reenviar com a mesma chave
  }

  async function enviar(ordem: NovaOrdem) {
    if (emAndamento.current) return;
    emAndamento.current = true;
    setEstado({ tipo: "enviando" });
    try {
      const res = await fetch("/api/ordens", {
        method: "POST",
        headers: { "content-type": "application/json", "idempotency-key": chaveDaBoleta },
        body: JSON.stringify(ordem),
        signal: AbortSignal.timeout(15_000),
      });
      if (res.status === 201) return setEstado({ tipo: "aceita", ordem: await res.json() });
      if (res.status >= 400 && res.status < 500 && res.status !== 409)
        return setEstado({ tipo: "rejeitada", motivo: await res.text() });
      throw new Error(`HTTP ${res.status}`);   // 5xx, timeout e 409 caem no polling
    } catch {
      setEstado({ tipo: "verificando" });
      setEstado(await verificar());
    } finally {
      emAndamento.current = false;
    }
  }

  return { estado, enviar };
}
```

---

## 5) O que ninguém pediu

Quatro melhorias, nessa ordem:

1. **Segredos e dados de investidores** (risco de dados e legal, esforço baixo, dano irreversível se vazar).
   - Tirar a credencial da task e levar pro Secrets Manager, e **rotacionar**. Revisão de task definition é imutável, então a senha antiga continua visível pra quem tem `ecs:DescribeTaskDefinition`.
   - Um usuário de banco por serviço, com privilégio mínimo; IAM restrito por fila e por segredo.
   - Mascaramento de dados pessoais no logger, retenção menor e o DPO acionado por causa da LGPD.
2. **Alertas, SLO e runbooks** (confiabilidade; o incidente foi descoberto pelo cliente).
   - SLO do POST /ordens: 99,9% sem 5xx e p95 abaixo de 500 ms, com alarme de consumo rápido do orçamento de erro (taxa acima de 1,44% em 5 min e em 1 h).
   - Alarmes que teriam tocado ontem antes do cliente avisar, todos indo pro plantão da squad via Opsgenie ou PagerDuty:
     - p95 do POST acima de 1 s por 3 minutos;
     - `DatabaseConnections` acima de 80% do orçamento;
     - qualquer ocorrência de "connection pool has been exhausted" no log;
     - idade da mensagem mais antiga acima de 2 minutos;
     - DLQ com qualquer mensagem.
   - Um alarme extra, com destino também em Operações: distância entre chegada e registro da ordem acima de 5 s.
   - Cada alarme aponta pra um runbook com os primeiros 10 minutos.
3. **Correlation ID, log estruturado e tracing** (velocidade de diagnóstico). Hoje ninguém consegue responder "o que aconteceu com a ordem X", e por isso a DLQ virou mistério. O ID da requisição do API Gateway vira o correlation ID, propagado em header HTTP, atributo da mensagem SQS e logs JSON do .NET e do Node. OpenTelemetry pro X-Ray, com amostragem de 5% e 100% dos erros e requisições lentas, pra segurar o custo.
4. **Entrega segura** (risco de mudança). Feature flag pra tudo que mexe no caminho crítico, deploy canário no ECS com rollback automático por alarme, congelamento de deploys das 13h30 às 15h00 verificado pelo pipeline e rollback ensaiado uma vez por mês, com meta de menos de 10 minutos.

**Como eu defendo com o PO:** falo de custo, não de boas práticas.
- O incidente custou 37 reclamações formais, possível reparação financeira, horas de Relacionamento e Operações, exposição regulatória e dias da squad dedicados ao incidente.
- Peço cerca de 20% da capacidade da sprint por três sprints, não uma parada de roadmap, começando pelo que sai rápido (alarme e rotação de credencial saem em dias).
- Mostro uma métrica: tempo pra detectar incidente, de "o cliente avisou" pra minutos.
- Flag e deploy canário aceleram o próprio roadmap, porque dá pra lançar com menos medo.
- O item 1 não entra em negociação com o PO: vai direto pra Segurança e pro DPO.

**Riscos que não foram citados:**
- **Webhooks sem assinatura e URLs cadastradas pelo distribuidor sem validação:** autenticidade e SSRF, por exemplo apontando pra `169.254.169.254`.
- **Sem rate limit por distribuidor:** um único cliente derruba os outros 379.
- **Sem reconciliação diária com o sistema de processamento:** se tivesse, as duplicadas teriam aparecido no mesmo dia.
- **Lambda de relatórios lendo do primário** no horário de pico.
- **Backup e restauração pontual nunca testados.**
- **Fuso, feriados e corte por fundo:** Fargate em UTC e regulamentos diferentes por fundo.

### Como eu levo sustentação

> **Da minha experiência.** No praiaki-digital trabalho em rodadas de reteste em staging, e cada rodada vira um PR rastreável com os achados (ex.: #234 "UX retest", #236 "staging retest", #262). Os achados vêm em boa parte dos meus agentes de QA e UX. A regra que eu sigo: o que volta num reteste ganha teste e dono, não só correção pontual.

Pra priorizar chamado em cima de legado, eu olho nessa ordem:
1. **Impacto no cliente:** quem tá parado ou recebendo valor errado passa na frente de incômodo.
2. **Risco financeiro ou regulatório:** tudo que mexe em cota, corte ou ordem duplicada vem antes do resto.
3. **Dono por causa raiz:** chamado parecido vira um problema só, com um responsável, em vez de ser fechado um a um.

Pra reduzir recorrência, chamado que volta só fecha com causa raiz registrada e teste de regressão. A meta é a lista de reabertos encolher, e isso eu meço no próprio histórico de chamados, sem número chutado.

---

## 6) Uso de IA no dia a dia

### Como eu trabalho hoje
- **Grok Bot como time:** em cada projeto tenho agentes por papel (QA, UX, segurança, infraestrutura e jurídico) coordenados em chats em grupo, cada um respondendo pela sua especialidade. Rotinas agendadas cuidam de checagem e resumo recorrente sem eu precisar pedir.
- **Cursor:** edição no repositório e agentes na nuvem pra tarefas longas, com roteamento por custo. Modelo barato pra trabalho leve (renomear, teste óbvio, documentação) e modelo forte pra bug difícil, concorrência e segurança.
- **GitHub Copilot:** autocomplete e primeira passada em PR.
- **Gemini:** segunda opinião quando o diagnóstico tá incerto.

### Neste cenário
| Tarefa | Quem | O que peço | Onde confio | Onde valido |
|---|---|---|---|---|
| Logs e métricas | Agente de infra + Cursor, só leitura | Consultas Logs Insights, painel JSON, linha do tempo 13h30–15h | Sintaxe, porque executo e vejo | A causa. Cruzo com Gemini e, principalmente, com a métrica real: duas IAs concordando não é evidência |
| Testes em base sem cobertura | Agente QA + Cursor (modelo barato) | Caracterização e bordas do corte (14:29:59.999, feriado, UTC) | Estrutura | Mutação: quebro o código e o teste tem que falhar |
| Consumidor e idempotência | Cursor, modelo forte | Revisão de corrida, lease e SIGTERM | Sugestões | Teste de concorrência real (Testcontainers/LocalStack) |
| PR de 1.800 linhas | Copilot + agentes de QA e segurança | N+1, `.Result`, chamada em loop, segredo, PII em log | Achados mecânicos | Regra de negócio e desenho continuam comigo |
| Runbook e comunicação | Grok Bot; o agente jurídico revisa a nota pra Relacionamento | Rascunho e caça a promessas indevidas | Clareza | Todo fato, prazo e compromisso. A IA não promete reparação |
| Acompanhamento | Rotina do Grok Bot | Resumo diário dos 37 casos e da DLQ até fechar | Formato | Os números vêm de SQL, não do modelo |

### No produto
**Valor real:** extração de boletas enviadas por e-mail/PDF (rascunho que um humano confirma); triagem assistida de exceções da DLQ e da reconciliação pra Operações; "onde tá minha ordem?" respondido a partir de status consultado de forma determinística; detecção de duplicidade e anomalias (muitas vezes ML clássico basta).
**Enfeite ou risco:** chatbot genérico; recomendação de fundo (suitability); qualquer IA no caminho síncrono da ordem; IA decidindo D0 ou D+1.
**O que não faço agora:** levar IA pro produto antes de estabilizar a plataforma; primeiro idempotência, horário de corte e observabilidade.

### Riscos
- **Alucinação em diagnóstico:** trato como hipótese até a métrica confirmar.
- **Dados sensíveis e LGPD:** só ferramentas aprovadas, modo de privacidade e retenção zero. Mascaro CPF/PII antes de colar log, e segredo nunca entra em prompt.
- **Segurança:** agente com acesso a produção é sempre só leitura, porque log com texto de usuário é vetor de prompt injection.
- **Custo:** roteamento por modelo, barato por padrão e forte só onde errar é caro. No produto, custo por chamada × 22 mil ordens/dia.
- **Dependência:** o plantão precisa saber investigar sem IA. Vários agentes concordando não substituem entendimento.

---

## Anexo: material de execução (opcional)

A pasta [`anexo/`](anexo/) tem o material pra executar esse plano. Não é implementação do portal; é o que eu levaria pra sala de guerra e pros PRs de correção. O índice tá em [`anexo/README.md`](anexo/README.md).

O consumidor do integrador e o C# (corte, idempotência e consulta de posições) rodam fora do ambiente real, com testes automatizados: 12 do integrador e 18 do C#, esses com Postgres em container.

```bash
cd anexo/07-raiz/integrador
npm install
npm test
```

Pro C# (precisa de .NET e Docker):

```bash
cd anexo/07-raiz/portal-minimo
dotnet test
```
