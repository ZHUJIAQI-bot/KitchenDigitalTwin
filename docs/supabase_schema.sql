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

-- =============================================================
-- 联机：房间 + 成员 + 聊天
-- =============================================================
create table if not exists rooms (
  id         uuid primary key default gen_random_uuid(),
  code       text not null unique,
  created_at timestamptz not null default now()
);

create table if not exists room_players (
  room_id      uuid not null references rooms(id) on delete cascade,
  username     text not null,
  display_name text not null default '',
  coat         integer not null default 0,
  trouser      integer not null default 0,
  pos_x        double precision not null default 0,
  pos_y        double precision not null default 0,
  pos_z        double precision not null default 0,
  rot_y        double precision not null default 0,
  updated_at   timestamptz not null default now(),
  primary key (room_id, username)
);

create table if not exists messages (
  id           bigint generated always as identity primary key,
  room_id      uuid not null references rooms(id) on delete cascade,
  username     text not null,
  display_name text not null default '',
  text         text not null,
  created_at   timestamptz not null default now()
);

-- 联机表的 RLS 策略（公开读写，同前）
alter table rooms enable row level security;
alter table room_players enable row level security;
alter table messages enable row level security;

drop policy if exists "public_rooms" on rooms;
drop policy if exists "public_room_players" on room_players;
drop policy if exists "public_messages" on messages;

create policy "public_rooms" on rooms for all using (true) with check (true);
create policy "public_room_players" on room_players for all using (true) with check (true);
create policy "public_messages" on messages for all using (true) with check (true);

-- 房间共享游戏状态（工单列表 JSON），用于联机一起修
create table if not exists room_state (
  room_id    uuid primary key references rooms(id) on delete cascade,
  orders     jsonb not null,
  updated_at timestamptz not null default now()
);

alter table room_state enable row level security;
drop policy if exists "public_room_state" on room_state;
create policy "public_room_state" on room_state for all using (true) with check (true);

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
