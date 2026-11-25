using PosApi.Common;

namespace PosApi.Features.Sales;

/// <summary>Bilingual messages for the SQL Server procedures' error numbers (old path, until customers are ported).</summary>
internal static class LegacySqlErrors
{
    internal static ApiError Translate(int number, string fallback) => number switch
    {
        /* sales.trg_Sale_RequireSession (migration 28): no open business session. */
        52090 => ApiError.Create("RESTAURANT_CLOSED",
            "Restaurant is currently closed. Open a business session before creating orders.",
            "المطعم مغلق حاليًا. افتح جلسة عمل قبل إنشاء الطلبات."),
        52000 => ApiError.Create("empty_basket",
            "A sale needs at least one line.", "الفاتورة تحتاج صنفًا واحدًا على الأقل."),
        52001 => ApiError.Create("bad_quantity",
            "Quantities must be above zero.", "يجب أن تكون الكميات أكبر من صفر."),
        52002 => ApiError.Create("unknown_item",
            "One of the items is no longer available.", "أحد الأصناف لم يعد متاحًا."),
        52003 => ApiError.Create("insufficient_stock",
            "There is not enough stock for one or more items.", "الكمية غير كافية لصنف أو أكثر."),
        52004 => ApiError.Create("discount_too_large",
            "The discount is more than the basket total.", "الخصم أكبر من إجمالي الفاتورة."),
        52005 => ApiError.Create("underpaid",
            "The payments do not cover the total.", "المدفوعات لا تغطي الإجمالي."),
        52006 => ApiError.Create("credit_needs_customer",
            "A credit sale needs a customer.", "البيع الآجل يحتاج إلى عميل."),
        52007 => ApiError.Create("over_credit_limit",
            "This would take the customer over their credit limit.",
            "هذا سيتجاوز حدّ الائتمان للعميل."),
        52010 => ApiError.Create("sale_not_found", "Sale not found.", "الفاتورة غير موجودة."),
        52011 => ApiError.Create("already_voided",
            "This sale is already voided.", "هذه الفاتورة ملغاة مسبقًا."),
        52012 => ApiError.Create("reason_required",
            "A void needs a reason.", "الإلغاء يحتاج إلى سبب."),
        52020 => ApiError.Create("bad_amount",
            "The amount must be above zero.", "يجب أن يكون المبلغ أكبر من صفر."),
        52021 => ApiError.Create("customer_not_found",
            "Customer not found.", "العميل غير موجود."),
        52022 => ApiError.Create("over_collection",
            "That is more than this customer owes.", "المبلغ أكبر مما على العميل."),
        _ => ApiError.Create("sale_failed", fallback, "تعذّر إتمام العملية.")
    };
}
