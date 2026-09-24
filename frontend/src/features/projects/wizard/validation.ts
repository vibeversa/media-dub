import { z } from 'zod';
import { isLanguageCode } from './languages.js';

/**
 * Wizard validation (Task 022, R1).
 *
 * Client validation is UX-only; the server stays authoritative and every 400
 * maps back to inline field errors with the draft preserved. Limits mirror the
 * backend exactly (`ProjectService` name/description, `ValidateLanguage`,
 * `ProjectProcessingSettingsValidator` shapes) so client-accepted drafts
 * rarely trip server validation. Pure: no React, no storage.
 */

const nameSchema = z
  .string()
  .trim()
  .min(1, 'Name is required.')
  .max(200, 'Name must be at most 200 characters.');

const descriptionSchema = z
  .string()
  .max(2000, 'Description must be at most 2000 characters.');

const languageCodeSchema = z
  .string()
  .trim()
  .min(2, 'Choose a language.')
  .max(3, 'Choose a language.')
  .refine((value) => isLanguageCode(value), 'Use a 2–3 letter language code.');

export const basicsSchema = z.object({
  name: nameSchema,
  description: descriptionSchema,
});

export type BasicsInput = z.input<typeof basicsSchema>;

export const languageSchema = z
  .object({
    sourceLanguage: languageCodeSchema,
    targetLanguage: languageCodeSchema,
  })
  .refine(
    (pair) => pair.sourceLanguage.trim().toLowerCase() !== pair.targetLanguage.trim().toLowerCase(),
    { message: 'Source and target languages must differ.', path: ['targetLanguage'] },
  );

export type LanguageInput = z.input<typeof languageSchema>;

const policyField = z
  .string()
  .trim()
  .min(1, 'Choose an option.')
  .max(64, 'Must be at most 64 characters.')
  .optional()
  .or(z.literal(''));

export const glossaryEntrySchema = z.object({
  sourceTerm: z.string().trim().min(1, 'Source term is required.').max(256, 'At most 256 characters.'),
  targetTerm: z.string().trim().min(1, 'Target term is required.').max(256, 'At most 256 characters.'),
  notes: z.string().max(1024, 'Notes must be at most 1024 characters.'),
});

export type GlossaryEntryInput = z.input<typeof glossaryEntrySchema>;

export const settingsSchema = z.object({
  sourceSeparationPolicy: policyField,
  outputProfile: policyField,
  timingStrictness: policyField,
  voicePolicy: policyField,
  reviewThreshold: z
    .number({ invalid_type_error: 'Threshold must be a number.' })
    .min(0, 'Threshold must be between 0 and 1.')
    .max(1, 'Threshold must be between 0 and 1.'),
  glossary: z.array(glossaryEntrySchema).max(1000, 'At most 1000 glossary entries.'),
  styleInstructions: z.string().max(4000, 'Style instructions must be at most 4000 characters.'),
});

export type SettingsInput = z.input<typeof settingsSchema>;

/** Field-error map: field path → message. Empty means valid. */
export type FieldErrors = Record<string, string>;

function toFieldErrors(error: z.ZodError): FieldErrors {
  const errors: Record<string, string> = {};
  for (const issue of error.issues) {
    const path = issue.path.map(String).join('.');
    const key = path === '' ? 'form' : path;
    if (errors[key] === undefined) {
      errors[key] = issue.message;
    }
  }
  return errors;
}

export function validateBasics(input: BasicsInput): FieldErrors {
  const parsed = basicsSchema.safeParse(input);
  return parsed.success ? {} : toFieldErrors(parsed.error);
}

export function validateLanguages(input: LanguageInput): FieldErrors {
  const parsed = languageSchema.safeParse(input);
  return parsed.success ? {} : toFieldErrors(parsed.error);
}

export function validateSettings(input: SettingsInput): FieldErrors {
  const parsed = settingsSchema.safeParse(input);
  return parsed.success ? {} : toFieldErrors(parsed.error);
}
