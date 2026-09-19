-- =============================================================
-- 焕新家装 · Supabase 数据库表结构
-- 在 Supabase 控制台 → SQL Editor 里整段粘贴运行一次即可。
-- =============================================================

-- 账号表：用户名为主键，密码存哈希（不是明文）
create table if not exists accounts (
  username      text primary key,
  password_hash text not null,
  display_name  text not null default '',
  coat          integer not null default 0,
  trouser       integer not null default 0,
  created_at    timestamptz not null default now()
);

-- 已存在的表补加姓名字段（幂等）
alter table accounts add column if not exists display_name text not null default '';

-- 存档表：每个账号一行，data 是 JSON 存档（收入/时间/工单/位置等）
create table if not exists saves (
  username   text primary key references accounts(username) on delete cascade,
  data       jsonb not null,
  updated_at timestamptz not null default now()
);

-- 更新存档时自动刷新 updated_at
create or replace function set_updated_at()
returns trigger as $$
begin
  new.updated_at = now();
  return new;
end;
$$ language plpgsql;

drop trigger if exists trg_saves_updated_at on saves;
create trigger trg_saves_updated_at
before update on saves
for each row execute function set_updated_at();

-- 简易鉴权辅助函数（可选）：供 RPC 调用做注册/登录校验。
-- 密码哈希沿用客户端算法（见 KitchenSimulator 的 AccountStore.Hash），
-- 这里只做存储，校验在客户端完成，因此默认关闭 RLS 以简化接入。
alter table accounts enable row level security;
alter table saves enable row level security;

drop policy if exists "public_read_accounts" on accounts;
drop policy if exists "public_write_accounts" on accounts;
drop policy if exists "public_update_accounts" on accounts;
drop policy if exists "public_read_saves" on saves;
drop policy if exists "public_write_saves" on saves;
drop policy if exists "public_update_saves" on saves;

create policy "public_read_accounts" on accounts for select using (true);
create policy "public_write_accounts" on accounts for insert with check (true);
create policy "public_update_accounts" on accounts for update using (true);
create policy "public_read_saves" on saves for select using (true);
create policy "public_write_saves" on saves for insert with check (true);
create policy "public_update_saves" on saves for update using (true);
