// Shapes returned by the FinGuard API (see /openapi/v1.json in development).

export type Role = 'Admin' | 'Analyst';
export interface User {
  id: string; email: string; displayName: string; role: Role; isActive: boolean; isDemo: boolean;
  createdAtUtc: string; lastLoginAtUtc: string | null; isLockedOut: boolean;
}

export interface Paged<T> { items: T[]; page: number; pageSize: number; total: number; totalPages: number }

export type RiskLevel = 'Low' | 'Medium' | 'High' | 'Critical';
export interface RiskFactor { key: string; label: string; points: number; maxPoints: number; detail: string }
export interface RiskAssessment {
  score: number; level: RiskLevel; confidence: string; recommendation: string; factors: RiskFactor[];
  trigger: string; assessedAtUtc: string;
}

export interface SupplierListItem {
  id: string; name: string; country: string; category: string; currency: string; riskScore: number; riskLevel: RiskLevel;
  status: 'Active' | 'Archived'; annualSpend: number; invoiceCount: number; openAlerts: number; isDemo: boolean; updatedAtUtc: string;
}
export interface SupplierFinancials {
  invoiceCount: number; totalInvoiced: number; pendingAmount: number; paidAmount: number; flaggedInvoices: number; openAlerts: number; averageInvoice: number;
}
export interface SupplierDetail {
  id: string; name: string; country: string; category: string; taxId: string | null; contactEmail: string | null; contactPhone: string | null;
  address: string | null; ibanMasked: string | null; ibanValid: boolean; ibanChangedAtUtc: string | null; currency: string; annualSpend: number;
  paymentTermsDays: number; notes: string | null; status: 'Active' | 'Archived'; onboardedOn: string; isDemo: boolean;
  risk: RiskAssessment | null; financials: SupplierFinancials;
}
export interface SupplierInput {
  name: string; country: string; category: string; taxId: string | null; contactEmail: string | null; contactPhone: string | null;
  address: string | null; iban: string | null; currency: string; annualSpend: number; paymentTermsDays: number; notes: string | null;
}
export interface RiskHistoryPoint { assessedAtUtc: string; score: number; level: RiskLevel; trigger: string }
export interface BankChange { changedAtUtc: string; oldIbanMasked: string | null; newIbanMasked: string | null }

export type Decision = 'AutoApprove' | 'ManualReview' | 'Reject';
export type InvoiceStatus = 'Pending' | 'Approved' | 'Rejected' | 'Paid';
export interface InvoiceLine { description: string; quantity: number; unitPrice: number }
export interface Discrepancy { code: string; severity: 'Info' | 'Warning' | 'Critical'; explanation: string }
export interface InvoiceListItem {
  id: string; number: string; supplierId: string; supplierName: string; invoiceDate: string; currency: string; total: number;
  status: InvoiceStatus; decision: Decision; verificationPoints: number; discrepancyCount: number; hasPdf: boolean; isDemo: boolean;
}
export interface InvoiceDetail {
  id: string; number: string; supplierId: string; supplierName: string; supplierRiskLevel: RiskLevel; poNumber: string | null;
  invoiceDate: string; dueDate: string | null; currency: string; total: number; linesTotal: number; ibanMasked: string | null;
  status: InvoiceStatus; decision: Decision; verificationPoints: number; discrepancies: Discrepancy[]; summary: string;
  verifiedAtUtc: string | null; createdBy: string | null; reviewedBy: string | null; reviewNote: string | null; reviewedAtUtc: string | null;
  pdfOriginalName: string | null; isDemo: boolean; lines: InvoiceLine[]; alerts: AlertListItem[];
  purchaseOrder: { number: string; currency: string; total: number; issuedOn: string; lines: InvoiceLine[] } | null;
  permissions: { canApprove: boolean; canReject: boolean; canMarkPaid: boolean; reason: string | null };
}
export interface InvoiceInput {
  supplierId: string; number: string; poNumber: string | null; invoiceDate: string; dueDate: string | null; currency: string;
  declaredTotal: number; iban: string | null; lines: InvoiceLine[];
}
export interface Extraction {
  textFound: boolean; number: string | null; invoiceDate: string | null; dueDate: string | null; total: number | null;
  currency: string | null; iban: string | null; poNumber: string | null; matchedSupplierId: string | null; matchedSupplierName: string | null; notes: string[];
}

export type Severity = 'Low' | 'Medium' | 'High' | 'Critical';
export type AlertStatus = 'Open' | 'Investigating' | 'Escalated' | 'Dismissed' | 'Resolved' | 'ConfirmedFraud';
export interface AlertListItem {
  id: string; number: number; code: string; severity: Severity; status: AlertStatus; title: string; supplierId: string; supplierName: string;
  invoiceId: string | null; invoiceNumber: string | null; assignedTo: string | null; exposure: number; isDemo: boolean; createdAtUtc: string; updatedAtUtc: string;
}
export interface AlertDetail {
  alert: AlertListItem; explanation: string; evidence: Record<string, string>; assignedToId: string | null; resolutionNote: string | null;
  events: { kind: string; fromStatus: string | null; toStatus: string | null; note: string | null; actorName: string; atUtc: string }[];
  allowedTransitions: AlertStatus[]; canEdit: boolean;
}

export interface NamedCount { name: string; count: number }
export interface Dashboard {
  kpis: {
    totalInvoices: number; verifiedInvoices: number; flaggedInvoices: number; rejectedBySystem: number; pendingReview: number;
    totalProcessed: number; financialExposure: number; openAlertExposure: number; highRiskSuppliers: number; activeSuppliers: number;
    openAlerts: number; criticalAlerts: number; confirmedFraud: number; baseCurrency: string; otherCurrencies: string[];
  };
  trend: { month: string; invoices: number; flagged: number; amount: number; flaggedAmount: number }[];
  riskDistribution: NamedCount[]; alertsBySeverity: NamedCount[]; alertsByStatus: NamedCount[]; topDiscrepancies: NamedCount[];
  topRiskSuppliers: { id: string; name: string; score: number; level: RiskLevel; openAlerts: number }[];
  recentActivity: Activity[]; spendByCategory: NamedCount[]; generatedAtUtc: string;
}
export interface Activity { atUtc: string; actor: string; action: string; entityType: string; entityId: string | null; details: string | null }

export interface ChatMessage { role: 'user' | 'assistant'; content: string }
export interface AssistantReply { reply: string; mode: 'live' | 'local' | 'local-fallback'; model: string | null; references: { type: string; id: string; label: string }[] }
export interface AssistantStatus { mode: 'live' | 'local'; provider: string | null; model: string | null; description: string }
export interface Priority { alertNumber: number; alertId: string; title: string; severity: Severity; status: string; supplier: string; supplierRisk: number; exposure: number; priorityScore: number }
export interface SystemInfo { name: string; environment: string; demoData: boolean; devOutbox: boolean }
export interface SystemSettings { approvalThreshold: number; baseCurrency: string; environment: string }
