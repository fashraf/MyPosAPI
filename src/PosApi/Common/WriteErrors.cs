namespace PosApi.Common;

/// <summary>
/// Arabic for the validation messages the write procedures raise.
///
/// A procedure throws one message, in English. Passing it into both slots would
/// show English to an Arabic cashier, which in a bilingual till is a bug rather
/// than a rough edge.
///
/// Keyed by error number, because the number is the stable identifier — the
/// English wording can be improved without breaking the translation.
/// </summary>
public static class WriteErrors
{
    private static readonly Dictionary<int, string> Arabic = new()
    {
        /* Catalogue */
        [59000] = "الاسم بالعربية والإنجليزية مطلوب.",
        [59001] = "لا يمكن أن يكون السعر بالسالب.",
        [59002] = "لا يمكن أن تكون التكلفة بالسالب.",
        [59003] = "هذا الباركود مستخدم لصنف آخر.",
        [59004] = "رمز الصنف مستخدم لصنف آخر.",
        [59005] = "لا يوجد صنف بهذا المعرّف.",
        [59010] = "الاسم بالعربية والإنجليزية مطلوب.",
        [59011] = "لا توجد فئة بهذا المعرّف.",

        /* Discounts */
        [59020] = "الخصم إما نسبة أو مبلغ.",
        [59021] = "النسبة يجب أن تكون أكبر من صفر و100 على الأكثر.",
        [59022] = "المبلغ يجب أن يكون أكبر من صفر.",
        [59023] = "تاريخ الانتهاء لا يمكن أن يسبق تاريخ البداية.",
        [59024] = "لا يوجد خصم بهذا المعرّف.",

        /* Users */
        [59030] = "اسم المستخدم مطلوب.",
        [59031] = "اسم المستخدم مستخدم بالفعل.",
        [59032] = "البريد الإلكتروني مستخدم لحساب آخر.",
        [59033] = "رقم الجوال مستخدم لحساب آخر.",
        [59034] = "لا يوجد دور بهذا المعرّف.",
        [59035] = "الحساب الخارجي يحتاج تاريخ انتهاء للوصول.",
        [59036] = "هذا هو المالك النشط الوحيد. عيّن مالكًا آخر أولًا.",
        [59037] = "لا يوجد مستخدم بهذا المعرّف.",

        /* Commission rules */
        [60020] = "أساس العمولة غير معروف.",
        [60021] = "قيمة العمولة يجب أن تكون أكبر من صفر.",
        [60022] = "نسبة العمولة لا يمكن أن تتجاوز 100.",
        [60023] = "لا توجد قاعدة عمولة بهذا المعرّف.",

        /* Bill templates */
        [60030] = "القالب يحتاج اسمًا.",
        [60031] = "حجم الورق يجب أن يكون 58mm أو 80mm أو A4.",
        [60032] = "إيصال العميل يجب أن يُظهر الإجمالي.",
        [60033] = "لا يوجد قالب بهذا المعرّف.",

        /* Branches */
        [61000] = "الاسم بالعربية والإنجليزية مطلوب.",
        [61001] = "رمز الفرع مطلوب.",
        [61002] = "رمز الفرع مستخدم بالفعل.",
        [61003] = "نسبة الضريبة يجب أن تكون بين 0 و100.",
        [61004] = "هذا هو الفرع النشط الوحيد. يجب أن يبقى فرع واحد على الأقل.",
        [61005] = "لا يوجد فرع بهذا المعرّف.",

        /* Company */
        [59040] = "الرقم الضريبي يجب أن يكون 15 رقمًا بالضبط.",
        [59041] = "لا يوجد سجل للشركة.",
    };

    /// <summary>
    /// The bilingual error for a procedure's exception.
    ///
    /// Falls back to the English text when a number has no translation yet —
    /// showing English twice is poor, but hiding the reason entirely is worse.
    /// </summary>
    public static ApiError From(Microsoft.Data.SqlClient.SqlException exception) =>
        ApiError.Create(
            "validation_failed",
            exception.Message,
            Arabic.TryGetValue(exception.Number, out var arabic) ? arabic : exception.Message);
}
