---
tags:
  - adr
  - dados
  - seguranca
  - jellyauth
created: 2026-09-23
status: aceita
---

# ADR-002 — Armazenamento de Códigos Temporários de Verificação

- **Data:** 2026-09-23
- **Status:** Aceita

## Contexto

No fluxo de cadastro, o servidor gera um código de 6 dígitos e o envia por e-mail. Entre o envio e a confirmação, o plugin precisa guardar:

- o **código** gerado;
- os **dados do cadastro pendente** (`username`, `email`, `password`);
- o **momento de expiração** (padrão 15 min);
- o **cooldown** de reenvio e os contadores de **rate limit**.

Precisávamos decidir onde persistir isso.

## Decisão

**Manter tudo em memória** (`ConcurrentDictionary`, chaveado por e-mail, com `TimeProvider` para relógio injetável), **sem persistir em disco**.

A senha em claro e o código só existem em RAM durante a janela curta de verificação e somem na expiração (ou reinício do servidor).

## Justificativa

1. **Segurança**: código e senha nunca tocam o disco. Um vazamento do filesystem não expõe senhas nem códigos válidos.
2. **Vida útil curtíssima**: os códigos expiram em minutos; persistir não traz valor e adiciona superfície de ataque.
3. **Instância única**: Jellyfin é um único processo; não há cenário de múltiplos nós precisando compartilhar estado.
4. **Simplicidade**: menos código de I/O e serialização para dados efêmeros.

## Consequências

### Positivas
- Código/senha nunca são gravados.
- Expiração trivial (`ExpiraEm` comparado ao `TimeProvider`).
- Fácil de testar (injeção de `TimeProvider`).

### Negativas / riscos aceitos
- **Reinício do servidor invalida cadastros pendentes** (o usuário refaz o pedido — impacto mínimo).
- **Rate limit também reseta** ao reiniciar. Em caso de abuso massivo, recomenda-se um limitador na borda (reverse proxy) — documentado como melhoria futura.

## O que **é** persistido (e por quê)

O mapeamento **e-mail → usuário** (para impedir contas duplicadas, já que o `User` nativo do Jellyfin não tem campo de e-mail) é persistido em JSON em `plugins/configurations/JellyAuth.cadastros.json` pela classe `ArmazenamentoCadastros`, com gravação atômica (padrão do JellyPix). Esse dado **não** é sensível e **deve** sobreviver a reinícios.

## Segurança adicional implementada

- Código numérico de 6 dígitos gerado com `RandomNumberGenerator.GetInt32` (sem viés de módulo).
- Comparação do código em **tempo constante** (`CryptographicOperations.FixedTimeEquals`), com trabalho equivalente quando não há pendente (sem timing side-channel de existência de e-mail).
- Máximo de tentativas de confirmação por código (5, via `Interlocked`); após exceder, o pendente é descartado.
- Mensagens genéricas em `Verify`/`Resend` e em duplicidade ("usuário ou e-mail já em uso") para não revelar qual existe.
- Rate limit **por e-mail e por IP** (`MaximoTentativasPorEmail` / `MaximoTentativasPorIp` dentro de `JanelaTentativasMinutos`), nos dois caminhos (com e sem verificação).
- **Poda periódica** (`RotinaLimpeza`, 5 min) + teto de 50k entradas para impedir crescimento sem limite (DoS de memória).
- **Senha SMTP em arquivo separado** (`ArmazenamentoSegredos`, modo 0600), nunca na configuração XML nem na API de config.

## Alternativas rejeitadas

| Alternativa | Motivo |
|---|---|
| JSON em disco (como os cadastros) | Gravaria senha/código; risco desnecessário para dado efêmero. |
| SQLite nativo (`jellyfin.db`) | Acoplamento alto à implementação interna; overkill para dados que expiram. |
| Cache distribuído (Redis) | Infraestrutura extra sem ganho num servidor de instância única. |
