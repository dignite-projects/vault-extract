import { Injectable, inject } from '@angular/core';
import { RestService } from '@abp/ng.core';
import { Observable } from 'rxjs';
import type {
  CabinetDto,
  CreateCabinetDto,
  UpdateCabinetDto,
} from '../../documents/cabinet.models';

// Backend: Dignite.Paperbase.HttpApi.Documents.CabinetController (/api/paperbase/cabinets).
@Injectable({ providedIn: 'root' })
export class CabinetService {
  private readonly rest = inject(RestService);
  private readonly apiName = 'Default';
  private readonly basePath = '/api/paperbase/cabinets';

  // 当前层全部文件柜（Host admin → Host 柜；租户 admin → 自己租户柜，不跨层 union）。
  getList = (): Observable<CabinetDto[]> =>
    this.rest.request<void, CabinetDto[]>(
      { method: 'GET', url: this.basePath },
      { apiName: this.apiName },
    );

  create = (input: CreateCabinetDto): Observable<CabinetDto> =>
    this.rest.request<CreateCabinetDto, CabinetDto>(
      { method: 'POST', url: this.basePath, body: input },
      { apiName: this.apiName },
    );

  update = (id: string, input: UpdateCabinetDto): Observable<CabinetDto> =>
    this.rest.request<UpdateCabinetDto, CabinetDto>(
      { method: 'PUT', url: `${this.basePath}/${id}`, body: input },
      { apiName: this.apiName },
    );

  delete = (id: string): Observable<void> =>
    this.rest.request<void, void>(
      { method: 'DELETE', url: `${this.basePath}/${id}` },
      { apiName: this.apiName },
    );
}
