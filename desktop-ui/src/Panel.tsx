import type {ReactNode} from "react";
export function Panel({ title, children, className = "", action }: {
  title: string; children: ReactNode; className?: string; action?: ReactNode
}) {
  return <section className={`glass panel ${className}`}>
    <div className="panel-head"><h2>{title}</h2>{action}</div>
    <div className="panel-inner">{children}</div>
  </section>;
}
