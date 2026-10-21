// Mirrors ImageTo3D.Api reference-search DTOs (src/backend/ImageTo3D.Api/DTOs/ReferenceDtos.cs).

import type { Issue } from './partDesign'

export type ReferenceDecision = 'pending' | 'accepted' | 'rejected'

export type ReferenceImageKind = 'unknown' | 'photo' | 'render' | 'drawing'

/** How a picture relates to the object being modelled; a similar look is not proof of the same object. */
export type ReferenceRelation = 'unknown' | 'same_object' | 'variant' | 'similar_different'

export type SourceStatus = 'ok' | 'empty' | 'failed' | 'timeout' | 'rate_limited' | 'disabled'

export type DownloadStatus = 'downloaded' | 'failed' | 'skipped'

export interface ReferenceSource {
  id: string
  name: string
  description: string
  enabled: boolean
  access: 'free_no_account'
}

/** Permissions are null when the licence is unknown: then the picture is for personal reference only. */
export interface ReferenceLicense {
  code: string
  label: string
  url: string | null
  raw: string | null
  commercialUse: boolean | null
  derivatives: boolean | null
  attributionRequired: boolean | null
}

export interface ReferenceIssue extends Omit<Issue, 'source'> {
  source: 'licence' | 'download' | 'duplicates'
}

export interface ReferenceCandidate {
  id: string
  origin: 'search' | 'manual'
  source: string
  sourceName: string
  title: string
  /** Web page describing the item (not an image). */
  pageUrl: string | null
  /** Direct address of the full image, when the source gives one. */
  imageUrl: string | null
  author: string | null
  license: ReferenceLicense
  attribution: string
  query: string | null
  retrievedAt: string
  downloadStatus: DownloadStatus
  downloadError: string | null
  hasThumbnail: boolean
  thumbnailWidth: number | null
  thumbnailHeight: number | null
  width: number | null
  height: number | null
  duplicateOfId: string | null
  duplicateKind: 'exact' | 'near' | null
  matchesInput: boolean
  decision: ReferenceDecision
  imageKind: ReferenceImageKind
  relation: ReferenceRelation
  note: string | null
  warnings: ReferenceIssue[]
}

export interface SourceSearchResult {
  source: string
  name: string
  status: SourceStatus
  hits: number
  added: number
  fromCache: boolean
  message: string | null
}

export interface ManualSearchLink {
  name: string
  url: string
  kind: 'images' | 'reference' | 'shop' | 'models'
}

export interface ReferenceSession {
  id: string
  name: string
  query: string
  sourceJobId: string | null
  hasInputImage: boolean
  inputWidth: number | null
  inputHeight: number | null
  createdAt: string
  updatedAt: string
  lastSearchedAt: string | null
  lastSearch: SourceSearchResult[]
  manualLinks: ManualSearchLink[]
  candidates: ReferenceCandidate[]
}

export interface ReferenceSessionSummary {
  id: string
  name: string
  query: string
  hasInputImage: boolean
  candidateCount: number
  acceptedCount: number
  createdAt: string
  updatedAt: string
}

export interface UpdateCandidateRequest {
  decision: ReferenceDecision
  imageKind?: ReferenceImageKind
  relation?: ReferenceRelation
  note?: string | null
}

export interface ManualPicture {
  file: File
  title?: string
  author?: string
  pageUrl?: string
  license?: string
  licenseUrl?: string
}

export interface ClearCacheResult {
  searchEntries: number
  files: number
  bytes: number
}
