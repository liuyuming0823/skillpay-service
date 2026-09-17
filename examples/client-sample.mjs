#!/usr/bin/env node
/**
 * 最小客户端示例（买家侧）。
 *
 * 演示完整的一轮交互，共四步：
 *   ① 请求付费资源 → 收到 402 与账单
 *   ② 解析账单，展示金额与收款方（真实支付由你的支付能力完成）
 *   ③ 带 Payment-Proof 重试同一请求
 *   ④ 解析两层 JSON，把交付物落盘并校验 SHA-256
 *
 * 用法：
 *   node examples/client-sample.mjs <资源地址> [Payment-Proof] [请求体JSON]
 *
 * 示例：
 *   # 第一步：拿账单
 *   node examples/client-sample.mjs http://127.0.0.1:18080/v1/skills/demo-file/result
 *
 *   # 第二步：完成支付后，带凭据重试（务必使用与第一步完全相同的请求体）
 *   node examples/client-sample.mjs http://127.0.0.1:18080/v1/skills/demo-file/result <proof>
 */

import { createHash } from 'node:crypto';
import { writeFile } from 'node:fs/promises';
import { basename, join } from 'node:path';

const [resourceUrl, paymentProof, bodyJson] = process.argv.slice(2);

if (!resourceUrl) {
  console.error('用法：node examples/client-sample.mjs <资源地址> [Payment-Proof] [请求体JSON]');
  process.exit(2);
}

/** 交付物落盘目录，默认当前目录。 */
const OUTPUT_DIR = process.env.OUTPUT_DIR ?? '.';

/** 客户端超时。交付大文件或现场生成内容时请调大。 */
const TIMEOUT_MS = Number(process.env.TIMEOUT_MS ?? 120_000);

function fromBase64Url(value) {
  const normalized = value.replace(/-/g, '+').replace(/_/g, '/');
  const padded = normalized + '='.repeat((4 - (normalized.length % 4)) % 4);
  return Buffer.from(padded, 'base64').toString('utf8');
}

async function request() {
  const headers = {};

  if (paymentProof) {
    headers['Payment-Proof'] = paymentProof;
  }

  if (bodyJson) {
    headers['Content-Type'] = 'application/json; charset=utf-8';
  }

  return fetch(resourceUrl, {
    method: bodyJson ? 'POST' : 'GET',
    headers,
    body: bodyJson ?? undefined,
    signal: AbortSignal.timeout(TIMEOUT_MS)
  });
}

async function main() {
  const response = await request();

  // ---- ① 402：需要支付 ------------------------------------------------
  if (response.status === 402) {
    const raw = response.headers.get('payment-needed');

    if (!raw) {
      console.error('收到 402，但没有 Payment-Needed 头，无法定位问题。');
      process.exitCode = 1;
      return;
    }

    const bill = JSON.parse(fromBase64Url(raw));

    console.log('需要支付');
    console.log('─'.repeat(48));
    console.log(`  商品    ${bill.method.goods_name}`);
    console.log(`  收款方  ${bill.method.seller_name}`);
    console.log(`  金额    ${bill.protocol.amount} ${bill.protocol.currency}`);
    console.log(`  订单号  ${bill.protocol.out_trade_no}`);
    console.log(`  截止    ${bill.protocol.pay_before}`);
    console.log(`  资源    ${bill.protocol.resource_id}`);
    console.log('─'.repeat(48));
    console.log('');
    console.log('② 在这里完成支付，拿到 Payment-Proof 后重跑本脚本：');
    console.log(`   node ${basename(process.argv[1])} '${resourceUrl}' <Payment-Proof>`);
    console.log('');
    console.log('   注意：重试时必须原样发送同一请求体，否则会出现「付 A 的钱、拿 B 的货」。');
    return;
  }

  // ---- 其他错误 --------------------------------------------------------
  if (!response.ok) {
    const text = await response.text();
    console.error(`请求失败：HTTP ${response.status}`);
    console.error(text);
    process.exitCode = 1;

    if (response.status >= 500) {
      console.error('');
      console.error('服务端错误：请保留凭据，稍后用同一凭据重试，不要重新发起支付。');
    }

    return;
  }

  // ---- ③ 200：交付成功 -------------------------------------------------
  const outer = await response.json();

  console.log(`交付成功  trade_no=${outer.trade_no}`);
  console.log(`           already_fulfilled=${outer.already_fulfilled}`);

  // ④ content 是 JSON 字符串，需要再解析一层
  let inner;

  try {
    inner = JSON.parse(outer.content);
  } catch {
    console.error('content 不是合法 JSON，原样输出：');
    console.log(outer.content);
    return;
  }

  const delivery = inner.delivery;

  if (!delivery) {
    console.error('响应里没有 delivery 字段，原样输出：');
    console.log(JSON.stringify(inner, null, 2));
    return;
  }

  if (delivery.type === 'file_base64') {
    const buffer = Buffer.from(delivery.content_base64, 'base64');
    const actual = createHash('sha256').update(buffer).digest('hex');
    const target = join(OUTPUT_DIR, delivery.file_name || 'delivered.bin');

    await writeFile(target, buffer);

    console.log(`文件      ${target}`);
    console.log(`大小      ${buffer.length} 字节（声明 ${delivery.size_bytes}）`);
    console.log(`SHA-256   ${actual}`);

    if (actual !== delivery.sha256) {
      console.error('');
      console.error('交付文件校验失败：实际摘要与声明不一致。');
      process.exitCode = 1;
      return;
    }

    console.log('校验通过');
    return;
  }

  if (delivery.type === 'text') {
    const target = join(OUTPUT_DIR, delivery.file_name || 'delivered.txt');
    await writeFile(target, delivery.content, 'utf8');

    console.log(`文件      ${target}`);
    console.log('---');
    console.log(delivery.content);
    return;
  }

  console.log(`未知交付类型：${delivery.type}`);
  console.log(JSON.stringify(delivery, null, 2));
}

main().catch((error) => {
  console.error('执行异常：', error.message);

  if (error.name === 'TimeoutError' || error.name === 'AbortError') {
    console.error('');
    console.error('请求超时。服务端的履约与连接是脱钩的——');
    console.error('请用同一 Payment-Proof 重试取回结果，不要重新发起支付。');
  }

  process.exitCode = 1;
});
