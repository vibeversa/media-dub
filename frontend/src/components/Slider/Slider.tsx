import type { InputHTMLAttributes, ReactNode } from 'react';
import { useId } from 'react';

export interface SliderProps extends InputHTMLAttributes<HTMLInputElement> {
  readonly label: string;
}

/** Native range slider with value readout. */
export function Slider({ label, id, ...rest }: SliderProps): ReactNode {
  const autoId = useId();
  const fieldId = id ?? `slider-${autoId}`;
  return (
    <>
      <style>{`.dp-slider{display:flex;flex-direction:column;gap:var(--space-1)}.dp-slider input{accent-color:var(--color-brand)}`}</style>
      <div className="dp-slider">
        <label className="dp-label" htmlFor={fieldId}>
          {label}
          {rest.value !== undefined ? ` (${String(rest.value)})` : null}
        </label>
        <input id={fieldId} type="range" className="dp-focus-ring" {...rest} />
      </div>
    </>
  );
}
