type EmptyStateProps = {
  message: string;
  small?: boolean;
};

export function EmptyState({ message, small }: EmptyStateProps) {
  return (
    <div
      style={{
        height: "100%",
        display: "grid",
        placeItems: "center",
        color: "#6b7280",
        fontSize: small ? 12 : 14,
      }}
    >
      {message}
    </div>
  );
}
