import { useEffect, useRef, useState } from 'react';
import mermaid from 'mermaid';

mermaid.initialize({
  startOnLoad: false,
  theme: 'dark',
  themeVariables: {
    primaryColor: '#3b82f6',
    primaryTextColor: '#f4f4f5',
    primaryBorderColor: '#3f3f46',
    lineColor: '#52525b',
    secondaryColor: '#27272a',
    tertiaryColor: '#18181b',
    fontFamily: 'ui-sans-serif, system-ui, sans-serif',
    fontSize: '13px',
    nodeBorder: '#3f3f46',
    mainBkg: '#27272a',
    clusterBkg: '#18181b',
    clusterBorder: '#3f3f46',
    titleColor: '#a1a1aa',
    edgeLabelBackground: '#18181b',
    nodeTextColor: '#e4e4e7',
  },
  flowchart: {
    curve: 'basis',
    padding: 16,
    htmlLabels: true,
  },
});

interface Props {
  chart: string;
  title?: string;
}

export default function MermaidDiagram({ chart, title }: Props) {
  const ref = useRef<HTMLDivElement>(null);
  const [error, setError] = useState(false);

  useEffect(() => {
    if (!ref.current) return;

    const id = `mermaid-${Math.random().toString(36).slice(2, 9)}`;

    mermaid.render(id, chart)
      .then(({ svg }) => {
        if (ref.current) {
          ref.current.innerHTML = svg;
          setError(false);
        }
      })
      .catch(() => {
        setError(true);
      });
  }, [chart]);

  if (error) {
    return (
      <pre className="bg-[#18181b] border border-zinc-800 rounded-lg p-4 text-[13px] text-zinc-400 font-mono overflow-x-auto">
        {chart}
      </pre>
    );
  }

  return (
    <div className="my-6">
      {title && (
        <h4 className="text-xs font-semibold text-zinc-500 uppercase tracking-wider mb-3">{title}</h4>
      )}
      <div className="bg-[#18181b] border border-zinc-800 rounded-lg p-6 overflow-x-auto">
        <div ref={ref} className="flex justify-center [&_svg]:max-w-full" />
      </div>
    </div>
  );
}
