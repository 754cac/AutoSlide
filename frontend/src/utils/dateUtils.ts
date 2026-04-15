function parseDateValue(value) {
  if (!value) return null;

  if (value instanceof Date) {
    return Number.isNaN(value.getTime()) ? null : new Date(value.getTime());
  }

  const rawValue = String(value).trim();
  if (!rawValue) return null;

  const hasTimezone = /([zZ]|[+-]\d{2}:?\d{2})$/.test(rawValue);
  const normalizedValue = hasTimezone ? rawValue : rawValue.replace(' ', 'T') + 'Z';
  const date = new Date(normalizedValue);

  return Number.isNaN(date.getTime()) ? null : date;
}

const HKT_DATE_TIME_FORMATTER = new Intl.DateTimeFormat('en-HK', {
  timeZone: 'Asia/Hong_Kong',
  year: 'numeric',
  month: 'short',
  day: 'numeric',
  hour: 'numeric',
  minute: '2-digit',
  hour12: true,
});

export function formatHKT(utcValue) {
  const date = parseDateValue(utcValue);
  if (!date) return '';

  return HKT_DATE_TIME_FORMATTER.format(date);
}

export function formatLocalDateTime(utcValue) {
  return formatHKT(utcValue);
}
