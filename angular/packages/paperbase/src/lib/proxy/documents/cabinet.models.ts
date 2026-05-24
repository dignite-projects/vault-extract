import type { EntityDto } from '@abp/ng.core';

// Mirrors C# Dignite.Paperbase.Documents.CabinetDto (#194).
// 文件柜——人工组织归属维度，与 DocumentType 正交。两层独立单层：GetList 返回当前层
// （Host admin → Host 柜；租户 admin → 自己租户柜，不跨层 union）。Guid 主键 + 名称层内唯一，
// 无字符串标识码（不进 LLM / 不被下游按 code 路由）。
export interface CabinetDto extends EntityDto<string> {
  tenantId?: string;
  displayName: string;
}

export interface CreateCabinetDto {
  displayName: string;
}

export interface UpdateCabinetDto {
  displayName: string;
}
