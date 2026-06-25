import type { EntityDto } from '@abp/ng.core';

export interface CabinetDto extends EntityDto<string> {
  tenantId?: string | null;
  name?: string;
  description?: string | null;
}

export interface CreateCabinetDto {
  name: string;
  description?: string | null;
}

export interface UpdateCabinetDto {
  name: string;
  description?: string | null;
}
